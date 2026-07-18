using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Watches the OS for the currently playing track. The service keeps <see cref="Current"/>
/// up to date on a background loop; consumers poll it (typically from a UI timer).
/// </summary>
public interface IMediaSessionService : IDisposable
{
    bool IsSupported { get; }

    string UnsupportedReason { get; }

    MediaSnapshot? Current { get; }

    void Start();
}
