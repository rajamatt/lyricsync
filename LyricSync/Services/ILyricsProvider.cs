using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>Raw lyrics as returned by a provider, before parsing/validation.</summary>
public sealed record ProviderLyrics(string? SyncedLrc, string? PlainLyrics, bool Instrumental);

/// <summary>
/// One lyrics database. Providers are tried in order by <see cref="LyricsService"/>;
/// the first one returning parseable synced lyrics wins.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Display name shown in the UI ("Synced lyrics · 42 lines · NetEase").</summary>
    string Name { get; }

    /// <summary>Returns null when the provider has nothing for this track.</summary>
    Task<ProviderLyrics?> TryGetAsync(TrackInfo track, CancellationToken ct);
}

/// <summary>
/// Candidate filtering shared by the search-based providers. Search endpoints return
/// loosely related songs, and attaching the wrong song's lyrics is worse than finding
/// none — so a candidate must plausibly match by title AND artist before its lyrics
/// are even fetched, and candidates are tried closest-duration first.
/// </summary>
internal static class LyricsCandidateMatcher
{
    public static bool Matches(TrackInfo track, string? candidateTitle, string? candidateArtist)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var trackTitle = Normalize(BaseTitle(track.Title));
        var candTitle = Normalize(candidateTitle);
        if (!candTitle.Contains(trackTitle) && !trackTitle.Contains(candTitle))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(track.Artist))
        {
            return true;
        }

        var trackArtist = Normalize(PrimaryArtist(track.Artist));
        var candArtist = Normalize(candidateArtist ?? string.Empty);
        return candArtist.Contains(trackArtist) || (trackArtist.Length > 0 && trackArtist.Contains(PrimaryArtist(candArtist)));
    }

    /// <summary>
    /// Search text for the provider queries: base title (edition suffixes like
    /// " - Remastered" stripped, matching what <see cref="Matches"/> compares) + artist.
    /// </summary>
    public static string SearchQuery(TrackInfo track) => $"{BaseTitle(track.Title)} {track.Artist}".Trim();

    /// <summary>Orders candidates by duration proximity (unknown durations last, original order kept).</summary>
    public static IEnumerable<T> ByDurationProximity<T>(IEnumerable<T> candidates, Func<T, double?> durationSeconds, TrackInfo track) =>
        track.DurationSeconds > 0
            ? candidates.OrderBy(c => durationSeconds(c) is double d and > 0
                ? Math.Abs(d - track.DurationSeconds)
                : double.MaxValue)
            : candidates;

    /// <summary>"Wonderwall - Remastered" → "Wonderwall".</summary>
    private static string BaseTitle(string title)
    {
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        var trimmed = dash > 0 ? title[..dash] : title;
        return trimmed.Trim();
    }

    /// <summary>"The Fray feat. X, Y" → "The Fray".</summary>
    private static string PrimaryArtist(string artist)
    {
        foreach (var separator in new[] { ",", " feat", " ft.", " & ", ";", "/" })
        {
            var index = artist.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                artist = artist[..index];
            }
        }

        return artist.Trim();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
