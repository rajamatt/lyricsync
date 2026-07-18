extern alias winsdk;

using System.Runtime.Versioning;
using LyricSync.Models;
using winsdk::System;
using winsdk::Windows.Media.Control;

namespace LyricSync.Services;

/// <summary>
/// Reads the currently playing track from the Windows system media bus
/// (Global System Media Transport Controls), the same source the volume
/// flyout uses. Works with the Spotify desktop and Store apps without any
/// API keys or authentication.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsMediaSessionService : IMediaSessionService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private volatile MediaSnapshot? _current;

    public bool IsSupported => true;

    public string UnsupportedReason => string.Empty;

    public MediaSnapshot? Current => _current;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _current = await QueryAsync();
            }
            catch
            {
                // The session manager can go stale (e.g. explorer restart); re-request next tick.
                _current = null;
                _manager = null;
            }

            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<MediaSnapshot?> QueryAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        // Any media player is accepted; Spotify wins when several sessions exist,
        // otherwise the session Windows considers current (the one on the volume flyout).
        var sessions = _manager.GetSessions();
        GlobalSystemMediaTransportControlsSession? chosen = null;
        foreach (var session in sessions)
        {
            var sourceId = session.SourceAppUserModelId ?? string.Empty;
            if (sourceId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            {
                chosen = session;
                break;
            }
        }

        chosen ??= _manager.GetCurrentSession() ?? (sessions.Count > 0 ? sessions[0] : null);

        if (chosen is null)
        {
            return null;
        }

        var props = await chosen.TryGetMediaPropertiesAsync();
        if (props is null || string.IsNullOrWhiteSpace(props.Title))
        {
            return null;
        }

        var timeline = chosen.GetTimelineProperties();
        var playback = chosen.GetPlaybackInfo();

        var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = playback?.PlaybackRate ?? 1.0;
        var duration = (int)Math.Round((timeline.EndTime - timeline.StartTime).TotalSeconds);

        var track = new TrackInfo(
            props.Title ?? string.Empty,
            props.Artist ?? string.Empty,
            props.AlbumTitle ?? string.Empty,
            duration > 0 ? duration : 0,
            chosen.SourceAppUserModelId ?? string.Empty);

        return new MediaSnapshot(track, isPlaying, rate, timeline.Position - timeline.StartTime, timeline.LastUpdatedTime);
    }

    public void Dispose() => _cts.Cancel();
}
