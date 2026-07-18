using LyricSync.Interop;
using Microsoft.UI.Dispatching;

namespace LyricSync;

/// <summary>
/// Windows-only: keeps LyricSync in the system tray. The control window's <b>minimize</b>
/// button hides it to the tray (the overlay keeps running); the <b>close</b> button quits.
/// The tray menu reopens the window, toggles the overlay, or exits.
///
/// The control window is hidden and re-shown, NOT freed and rebuilt. Freeing its content
/// only reclaimed ~3 MB (the memory is the framework/Skia baseline, not the widgets), and
/// rebuilding a new MainPage each reopen leaked: the long-lived MVUX ViewModel holds a
/// change-notification reference to every view bound to it, so discarded MainPages were
/// never collected (~10 MB per reopen). Keeping one MainPage instance avoids both.
/// </summary>
public sealed class TrayHost
{
    private readonly Window _mainWindow;
    private readonly DispatcherQueue _dispatcher;

    private Win32TrayIcon? _tray;
    private nint _hwnd;
    private bool _exiting;
    private bool _shownTip;
    private DispatcherQueueTimer? _findTimer;

    public TrayHost(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _dispatcher = mainWindow.DispatcherQueue;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _tray = new Win32TrayIcon { IsOverlayShown = () => AppRoot.Overlay.IsShown };
        _tray.ShowSettings += ShowMainWindow;
        _tray.ToggleOverlay += () => AppRoot.Overlay.RequestToggle();
        _tray.Exit += ExitApp;
        _tray.Create("LyricSync");

        ResolveHwnd();
    }

    private void ResolveHwnd()
    {
        _hwnd = Win32Overlay.FindProcessWindow("LyricSync");
        if (_hwnd != 0)
        {
            Win32MainWindow.InstallHooks(_hwnd, HideMainWindow, ExitApp);
            return;
        }

        // The native window may not carry its title yet right after Activate(); retry briefly.
        var attempts = 0;
        _findTimer = _dispatcher.CreateTimer();
        _findTimer.Interval = TimeSpan.FromMilliseconds(100);
        _findTimer.Tick += (timer, _) =>
        {
            _hwnd = Win32Overlay.FindProcessWindow("LyricSync");
            if (_hwnd != 0)
            {
                timer.Stop();
                Win32MainWindow.InstallHooks(_hwnd, HideMainWindow, ExitApp);
            }
            else if (++attempts > 30)
            {
                timer.Stop();
            }
        };
        _findTimer.Start();
    }

    private void HideMainWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_hwnd != 0)
        {
            Win32MainWindow.Hide(_hwnd);
        }

        // Note: the content is intentionally kept (not set to null). See the class remarks —
        // freeing it saved almost nothing and rebuilding it on reopen leaked memory.

        if (!_shownTip)
        {
            _shownTip = true;
            _tray?.ShowInfo("LyricSync", "Still running here — right-click the tray icon for options.");
        }
    }

    private void ShowMainWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_hwnd != 0)
        {
            Win32MainWindow.Show(_hwnd);
        }

        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try { _tray?.Dispose(); } catch { /* best effort */ }
        try { AppRoot.Shutdown(); } catch { /* best effort */ }
        Environment.Exit(0);
    }
}
