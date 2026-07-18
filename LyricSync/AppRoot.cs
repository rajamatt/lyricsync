using LyricSync.Services;

namespace LyricSync;

/// <summary>Composition root; wires the MVUX model, media service, and overlay together.</summary>
public static class AppRoot
{
    private static bool _initialized;
    private static IMediaSessionService _media = null!;

    public static AppSettings Settings { get; private set; } = null!;

    public static MainViewModel ViewModel { get; private set; } = null!;

    public static MainModel Model { get; private set; } = null!;

    public static OverlayController Overlay { get; private set; } = null!;

    public static void Initialize(Window mainWindow)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        Settings = SettingsStore.Load();

        _media = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            ? new WindowsMediaSessionService()
            : new NullMediaSessionService();

        Overlay = new OverlayController(mainWindow.DispatcherQueue, Settings);
        ViewModel = new MainViewModel(_media, new LyricsService(), Settings, Overlay);
        Model = ViewModel.Model;
        Overlay.Model = Model;

        _media.Start();
    }

    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        Overlay.Shutdown();
        _media.Dispose();
        SettingsStore.Save(Settings);
    }
}
