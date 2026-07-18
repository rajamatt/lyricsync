using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>Fallback for operating systems without a supported media session API.</summary>
public sealed class NullMediaSessionService : IMediaSessionService
{
    public bool IsSupported => false;

    public string UnsupportedReason => "Media detection requires Windows 10 (1809) or later.";

    public MediaSnapshot? Current => null;

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
