using System.Runtime.CompilerServices;
using LyricSync.Models;
using LyricSync.Services;
using Uno.Extensions.Reactive;

namespace LyricSync;

/// <summary>
/// The MVUX model. The whole app is a chain of feeds:
///
///   media bus ──► Playback (10 Hz position ticks, display-ready)
///        │
///        └──► Track (emits only when the song changes)
///                └─ SelectAsync ──► Lyrics (LRCLIB fetch, auto-cancelled on track change)
///                                      │
///   Playback × Lyrics ── Combine/Select ──► Display (current/previous/next line)
///
/// States hold the two user settings (font size, background opacity) and the
/// overlay flags; commands are generated from the public methods.
/// </summary>
public partial record MainModel(
    IMediaSessionService Media,
    LyricsService LyricsService,
    AppSettings Settings,
    OverlayController Overlay)
{
    // --- Playback: position/status snapshot, ticking ~10×/second -------------------

    public IFeed<NowPlaying> Playback => Feed.AsyncEnumerable(PollPlayback);

    private async IAsyncEnumerable<NowPlaying> PollPlayback([EnumeratorCancellation] CancellationToken ct = default)
    {
        NowPlaying? last = null;
        while (!ct.IsCancellationRequested)
        {
            var current = NowPlaying.From(Media.Current, Media.IsSupported ? null : Media.UnsupportedReason);
            if (current != last)
            {
                last = current;
                yield return current;
            }

            await Task.Delay(100, ct);
        }
    }

    // --- Track: emits only when the song (or its late-arriving duration) changes ----

    /// <summary>Bumped by <see cref="ClearLyricsCache"/> to force a re-fetch of the current song.</summary>
    private int _generation;

    public IFeed<TrackInfo> Track => Feed.AsyncEnumerable(TrackChanges);

    private async IAsyncEnumerable<TrackInfo> TrackChanges([EnumeratorCancellation] CancellationToken ct = default)
    {
        var last = TrackInfo.None;
        yield return last;

        while (!ct.IsCancellationRequested)
        {
            var track = (Media.Current?.Track ?? TrackInfo.None) with { Generation = _generation };
            if (track != last)
            {
                last = track;
                yield return track;
            }

            await Task.Delay(250, ct);
        }
    }

    // --- Lyrics: refetched automatically whenever Track changes ---------------------

    public IFeed<TrackLyrics> Lyrics => Track.SelectAsync(async (track, ct) =>
        track.IsEmpty
            ? TrackLyrics.None
            : new TrackLyrics(track, await LyricsService.GetLyricsAsync(track, ct)));

    public IFeed<string> LyricsStatusText => Lyrics.Select(l => l.StatusText);

    public IFeed<string> PlainLyricsText => Lyrics.Select(l => l.Result?.PlainLyrics ?? string.Empty);

    public IFeed<bool> IsPlainLyricsVisible => Lyrics.Select(l => l.Result?.Status == LyricsStatus.PlainOnly);

    public IListFeed<LyricRow> Lines => Lyrics.Select(BuildRows).AsListFeed();

    private static IImmutableList<LyricRow> BuildRows(TrackLyrics lyrics) =>
        lyrics.Result?.Status == LyricsStatus.Synced
            ? lyrics.Result.Lines
                .Select(line => new LyricRow(
                    $"{(int)line.Start.TotalMinutes}:{line.Start.Seconds:00}",
                    string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text))
                .ToImmutableList()
            : ImmutableList<LyricRow>.Empty;

    // --- Display: playback position × lyrics = the lines on the overlay --------------

    public IFeed<LyricsDisplay> Display => Feed
        .Combine(Playback, Lyrics)
        .Select(pair => LyricsDisplay.Compute(pair.Item1, pair.Item2));

    // --- User settings (two-way bound, persisted on change) --------------------------

    public IState<double> FontSize => State
        .Value(this, () => Settings.OverlayFontSize)
        .ForEach(async (value, ct) =>
        {
            Settings.OverlayFontSize = value;
            SettingsStore.Save(Settings);
        });

    public IFeed<double> SecondaryFontSize => FontSize.Select(f => Math.Round(f * 0.55));

    public IFeed<string> FontSizeLabel => FontSize.Select(v => $"{v:0} px");

    /// <summary>0–100, controls only the overlay's backdrop panel (a native layered window).</summary>
    public IState<double> BackgroundOpacity => State
        .Value(this, () => Math.Round(Settings.OverlayOpacity * 100))
        .ForEach(async (value, ct) =>
        {
            Settings.OverlayOpacity = value / 100.0;
            Overlay.SetPanelOpacity(value / 100.0);
            SettingsStore.Save(Settings);
        });

    public IFeed<string> BackgroundOpacityLabel => BackgroundOpacity.Select(v => $"{v:0}%");

    /// <summary>Hex color of the current lyric line (classic subtitle yellow by default).</summary>
    public IState<string> LyricColor => State
        .Value(this, () => Settings.LyricColor)
        .ForEach(async (value, ct) =>
        {
            if (!string.IsNullOrEmpty(value))
            {
                Settings.LyricColor = value;
                SettingsStore.Save(Settings);
            }
        });

    public async ValueTask SetLyricColor(string color, CancellationToken ct) =>
        await LyricColor.SetAsync(color, ct);

    // --- Overlay window flags (written back by the OverlayController) ----------------

    public IState<bool> IsOverlayShown => State.Value(this, () => Settings.OverlayVisible);

    public IState<bool> IsMovingOverlay => State.Value(this, () => false);

    public IFeed<string> OverlayButtonText => IsOverlayShown.Select(shown => shown ? "Hide overlay" : "Show overlay");

    public IFeed<string> MoveButtonText => IsMovingOverlay.Select(moving => moving ? "Lock overlay" : "Move overlay");

    public IFeed<double> MoveChromeOpacity => IsMovingOverlay.Select(moving => moving ? 1.0 : 0.0);

    // --- Commands (generated from these methods) --------------------------------------

    public async ValueTask ToggleOverlay(CancellationToken ct) => Overlay.RequestToggle();

    public async ValueTask ToggleMoveOverlay(CancellationToken ct) => Overlay.RequestToggleMoveMode();

    /// <summary>Wipes the lyrics caches and re-fetches lyrics for the current song.</summary>
    public async ValueTask ClearLyricsCache(CancellationToken ct)
    {
        LyricsService.ClearCache();
        Interlocked.Increment(ref _generation);
    }
}
