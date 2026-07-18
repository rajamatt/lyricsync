namespace LyricSync.Models;

/// <summary>A snapshot of playback shaped for display, produced ~10×/second.</summary>
public sealed record NowPlaying(
    TrackInfo? Track,
    bool IsPlaying,
    TimeSpan Position,
    string StatusText,
    string Title,
    string Subtitle,
    string PositionText,
    string DurationText,
    double Progress)
{
    public static NowPlaying From(MediaSnapshot? snapshot, string? unsupportedReason)
    {
        if (snapshot is null)
        {
            return new NowPlaying(
                Track: null,
                IsPlaying: false,
                Position: TimeSpan.Zero,
                StatusText: unsupportedReason ?? "Waiting for media…",
                Title: "Nothing playing",
                Subtitle: "Play a song in Spotify (or any player) to get started",
                PositionText: "0:00",
                DurationText: "0:00",
                Progress: 0);
        }

        var track = snapshot.Track;
        var position = snapshot.EstimatePosition(DateTimeOffset.UtcNow);
        var duration = track.DurationSeconds;

        return new NowPlaying(
            Track: track,
            IsPlaying: snapshot.IsPlaying,
            Position: position,
            StatusText: (snapshot.IsPlaying ? "Playing · " : "Paused · ") + SourceDisplayName(track.SourceApp),
            Title: track.Title,
            Subtitle: string.IsNullOrWhiteSpace(track.Album) || track.Album == track.Title
                ? track.Artist
                : $"{track.Artist} · {track.Album}",
            PositionText: $"{(int)position.TotalMinutes}:{position.Seconds:00}",
            DurationText: duration > 0 ? $"{duration / 60}:{duration % 60:00}" : "–:––",
            Progress: duration > 0 ? Math.Clamp(position.TotalSeconds / duration * 100, 0, 100) : 0);
    }

    private static string SourceDisplayName(string sourceAppId)
    {
        if (sourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        // Turn AUMIDs like "Microsoft.ZuneMusic_8wekyb3d8bbwe!App" or "msedge.exe" into something readable.
        var name = sourceAppId;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        var underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && lastDot < name.Length - 1)
        {
            name = name[(lastDot + 1)..];
        }

        return string.IsNullOrWhiteSpace(name) ? "media" : name;
    }
}

/// <summary>The lyrics fetched for a specific track (so stale results are detectable).</summary>
public sealed record TrackLyrics(TrackInfo Track, LyricsResult? Result)
{
    public static readonly TrackLyrics None = new(TrackInfo.None, null);

    public string StatusText => Result?.Status switch
    {
        LyricsStatus.Synced => $"Synced lyrics · {Result.Lines.Count} lines · LRCLIB",
        LyricsStatus.PlainOnly => "Lyrics found, but without timestamps",
        LyricsStatus.Instrumental => "Instrumental track",
        LyricsStatus.Error => "Could not reach the lyrics service",
        LyricsStatus.NotFound => "No lyrics found for this track",
        _ => string.Empty,
    };
}

/// <summary>A row of the lyric preview list in the main window.</summary>
public sealed record LyricRow(string TimeText, string Text);

/// <summary>Everything the overlay needs to render, computed from playback × lyrics.</summary>
public sealed record LyricsDisplay(
    int CurrentIndex,
    string TrackLabel,
    string Previous,
    string Current,
    string Next)
{
    public static readonly LyricsDisplay Idle = new(-1, "LyricSync", string.Empty, "Play some music…", string.Empty);

    public static LyricsDisplay Compute(NowPlaying playback, TrackLyrics lyrics)
    {
        if (playback.Track is null)
        {
            return Idle;
        }

        var label = playback.Track.Display;

        if (lyrics.Result is null || lyrics.Track.Key != playback.Track.Key)
        {
            return new LyricsDisplay(-1, label, string.Empty, "Searching lyrics…", string.Empty);
        }

        return lyrics.Result.Status switch
        {
            LyricsStatus.Synced => ComputeSynced(playback, lyrics.Result, label),
            LyricsStatus.PlainOnly => new LyricsDisplay(-1, label, string.Empty, "♪ No synced lyrics — see the LyricSync window", string.Empty),
            LyricsStatus.Instrumental => new LyricsDisplay(-1, label, string.Empty, "🎵 Instrumental", string.Empty),
            LyricsStatus.Error => new LyricsDisplay(-1, label, string.Empty, "⚠ Lyrics service unreachable", string.Empty),
            _ => new LyricsDisplay(-1, label, string.Empty, "No lyrics found", string.Empty),
        };
    }

    private static LyricsDisplay ComputeSynced(NowPlaying playback, LyricsResult result, string label)
    {
        var lines = result.Lines;
        var index = FindLineIndex(lines, playback.Position);

        return new LyricsDisplay(
            index,
            label,
            index > 0 ? DisplayText(lines[index - 1].Text) : string.Empty,
            index >= 0 ? DisplayText(lines[index].Text) : "♪",
            index + 1 < lines.Count ? DisplayText(lines[index + 1].Text) : string.Empty);
    }

    /// <summary>Last line whose start is ≤ the position; -1 when before the first line.</summary>
    private static int FindLineIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var lo = 0;
        var hi = lines.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Start <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private static string DisplayText(string text) => string.IsNullOrWhiteSpace(text) ? "♪" : text;
}
