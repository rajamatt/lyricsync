using LyricSync.Interop;
using LyricSync.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;

namespace LyricSync;

/// <summary>
/// Owns the lyrics overlay window: a borderless, always-click-through secondary window
/// that is kept above fullscreen apps (topmost re-asserted every second because games
/// steal it), excluded from Alt-Tab, and carried across virtual desktops. Click-through
/// is implemented with a WM_NCHITTEST override, which also provides OS-native dragging
/// while "move mode" is active. A DesktopAcrylicBackdrop gives the window a transparent
/// (frosted) background so the opacity slider only affects the overlay's own panel.
/// </summary>
public sealed class OverlayController
{
    private const string OverlayWindowTitle = "LyricSync Overlay";

    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;

    private Window? _window;
    private nint _hwnd;
    private nint _panelHwnd;
    private DispatcherQueueTimer? _keepAliveTimer;
    private DispatcherQueueTimer? _findHwndTimer;
    private bool _closingInternally;
    private bool _rectDirty;
    private bool _moveMode;

    public OverlayController(DispatcherQueue dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
    }

    /// <summary>Set by <see cref="AppRoot"/> once the model exists (states are pushed back to it).</summary>
    public MainModel? Model { get; set; }

    public bool IsShown => _window is not null;

    /// <summary>MVUX commands run on background threads; marshal to the UI thread.</summary>
    private void OnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    public void RequestToggle() => OnUiThread(() =>
    {
        if (IsShown)
        {
            Hide();
        }
        else
        {
            Show();
        }
    });

    public void RequestToggleMoveMode() => OnUiThread(() =>
    {
        if (!IsShown)
        {
            Show();
        }

        SetMoveMode(!_moveMode);
    });

    public void Show() => OnUiThread(() =>
    {
        if (_window is not null)
        {
            return;
        }

        var window = new Window { Title = OverlayWindowTitle };
        window.Content = new OverlayView();

        if (window.AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            Try(() => presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false));
            Try(() => presenter.IsAlwaysOnTop = true);
            Try(() => presenter.IsMaximizable = false);
            Try(() => presenter.IsMinimizable = false);
            Try(() => presenter.IsResizable = false);
        }

#if HAS_UNO
        // Pure black is this window's transparency key (see ApplyBlackColorKey), so an
        // explicitly black window background = a fully transparent background.
        Try(() => Uno.UI.Xaml.WindowHelper.SetBackground(
            window, new SolidColorBrush(Microsoft.UI.Colors.Black)));
#endif

        window.Closed += OnWindowClosed;
        _window = window;
        window.Activate();

        if (OperatingSystem.IsWindows())
        {
            LocateHwndAndApply();
        }
        else if (window.AppWindow is { } appWindow)
        {
            Try(() => appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1000, Height = 220 }));
        }

        _settings.OverlayVisible = true;
        SettingsStore.Save(_settings);
        PushStates(shown: true, moving: false);
    });

    public void Hide() => OnUiThread(() =>
    {
        if (_window is null)
        {
            return;
        }

        _settings.OverlayVisible = false;
        CloseWindow();
        SettingsStore.Save(_settings);
    });

    /// <summary>Closes the overlay on app shutdown without marking it user-hidden.</summary>
    public void Shutdown() => OnUiThread(CloseWindow);

    /// <summary>Live-updates the background panel's translucency (0 = invisible, 1 = solid).</summary>
    public void SetPanelOpacity(double opacity) => OnUiThread(() =>
    {
        if (OperatingSystem.IsWindows() && _panelHwnd != 0)
        {
            Win32Overlay.SetPanelAlpha(_panelHwnd, opacity);
            // Re-showing the panel can raise it in the z-order; keep it beneath the text.
            Win32Overlay.SyncPanelToOwner(_hwnd);
        }
    });

    private void SetMoveMode(bool moving)
    {
        _moveMode = moving;
        Win32OnWindows(() =>
        {
            Win32Overlay.SetMoveMode(moving);
            if (_hwnd != 0)
            {
                Win32Overlay.SetClickThrough(_hwnd, clickThrough: !moving);
            }
        });
        PushStates(shown: IsShown, moving: moving);
    }

    private void PushStates(bool shown, bool moving)
    {
        if (Model is { } model)
        {
            _ = model.IsOverlayShown.SetAsync(shown, CancellationToken.None);
            _ = model.IsMovingOverlay.SetAsync(moving, CancellationToken.None);
        }
    }

    private void LocateHwndAndApply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _hwnd = Win32Overlay.FindProcessWindow(OverlayWindowTitle);
        if (_hwnd != 0)
        {
            ApplyWin32Behaviors();
            return;
        }

        // The native window can materialize a frame after Activate(); retry briefly.
        var attempts = 0;
        _findHwndTimer = _dispatcher.CreateTimer();
        _findHwndTimer.Interval = TimeSpan.FromMilliseconds(100);
        _findHwndTimer.Tick += (timer, _) =>
        {
            if (!OperatingSystem.IsWindows() || _window is null)
            {
                timer.Stop();
                return;
            }

            _hwnd = Win32Overlay.FindProcessWindow(OverlayWindowTitle);
            if (_hwnd != 0)
            {
                timer.Stop();
                ApplyWin32Behaviors();
            }
            else if (++attempts > 30)
            {
                timer.Stop();
            }
        };
        _findHwndTimer.Start();
    }

    private void ApplyWin32Behaviors()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == 0)
        {
            return;
        }

        Win32Overlay.ApplyOverlayStyles(_hwnd, clickThrough: !_moveMode);
        Win32Overlay.InstallHitTestOverride(_hwnd);
        Win32Overlay.SetMoveMode(_moveMode);
        Win32Overlay.ApplyBlackColorKey(_hwnd);
        // The color key removes only pure-black pixels; the DWM frame border isn't black,
        // so it would show as a box outline. Strip it and the drop shadow too.
        Win32Overlay.RemoveDwmBorder(_hwnd);
        Win32Overlay.SuppressShadow(_hwnd);

        // The translucent background is a separate content-less window glued beneath
        // this one (color keying is binary, LWA_ALPHA on the panel gives the gradient).
        _panelHwnd = Win32Overlay.CreatePanelWindow();
        if (_panelHwnd != 0)
        {
            Win32Overlay.SetPanelPair(_panelHwnd);
            Win32Overlay.SetPanelAlpha(_panelHwnd, _settings.OverlayOpacity);
        }

        if (_settings is { OverlayX: int x, OverlayY: int y, OverlayWidth: > 0 and int w, OverlayHeight: > 0 and int h }
            && Win32Overlay.IsOnScreen(x, y, w, h))
        {
            Win32Overlay.Move(_hwnd, x, y, w, h);
        }
        else
        {
            Win32Overlay.MoveToDefaultPosition(_hwnd);
        }

        Win32Overlay.AssertTopmost(_hwnd);
        Win32Overlay.SyncPanelToOwner(_hwnd);

        _keepAliveTimer = _dispatcher.CreateTimer();
        _keepAliveTimer.Interval = TimeSpan.FromSeconds(1);
        _keepAliveTimer.Tick += (_, _) => KeepAliveTick();
        _keepAliveTimer.Start();
    }

    private void KeepAliveTick()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == 0)
        {
            return;
        }

        // Fullscreen games and desktop switches knock the overlay off the top of the
        // z-order; putting it back once a second is the same approach FPS counters use.
        Win32Overlay.AssertTopmost(_hwnd);
        Win32Overlay.EnsureOnCurrentDesktop(_hwnd);
        if (_panelHwnd != 0)
        {
            Win32Overlay.EnsureOnCurrentDesktop(_panelHwnd);
        }

        Win32Overlay.ApplyBlackColorKey(_hwnd);
        // Uno re-applies its own DWM frame extension on repaints, which can bring the
        // shadow back; keep it suppressed.
        Win32Overlay.SuppressShadow(_hwnd);
        Win32Overlay.SyncPanelToOwner(_hwnd);

        // Track the overlay rect and persist it once it stops changing, so the
        // position survives even if the app is killed without a clean shutdown.
        if (Win32Overlay.GetRect(_hwnd) is { } rect)
        {
            if (_settings.OverlayX != rect.Left || _settings.OverlayY != rect.Top
                || _settings.OverlayWidth != rect.Width || _settings.OverlayHeight != rect.Height)
            {
                _settings.OverlayX = rect.Left;
                _settings.OverlayY = rect.Top;
                _settings.OverlayWidth = rect.Width;
                _settings.OverlayHeight = rect.Height;
                _rectDirty = true;
            }
            else if (_rectDirty)
            {
                _rectDirty = false;
                SettingsStore.Save(_settings);
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_closingInternally)
        {
            return;
        }

        Cleanup();
    }

    private void CloseWindow()
    {
        _closingInternally = true;
        try
        {
            _window?.Close();
        }
        catch
        {
            // Closing an already-closed window is fine.
        }
        finally
        {
            _closingInternally = false;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _keepAliveTimer?.Stop();
        _keepAliveTimer = null;
        _findHwndTimer?.Stop();
        _findHwndTimer = null;

        if (OperatingSystem.IsWindows())
        {
            if (_hwnd != 0)
            {
                Win32Overlay.RemoveHitTestOverride(_hwnd);
            }

            if (_panelHwnd != 0)
            {
                Win32Overlay.DestroyPanelWindow(_panelHwnd);
            }
        }

        _window = null;
        _hwnd = 0;
        _panelHwnd = 0;
        _moveMode = false;

        if (OperatingSystem.IsWindows())
        {
            Win32Overlay.SetMoveMode(false);
        }

        PushStates(shown: false, moving: false);
    }

    private static void Win32OnWindows(Action action)
    {
        if (OperatingSystem.IsWindows())
        {
            action();
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Optional windowing capability not available on this platform/version.
        }
    }
}
