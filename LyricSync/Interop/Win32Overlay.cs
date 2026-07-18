using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LyricSync.Interop;

/// <summary>
/// Win32 plumbing that turns a plain window into a game-overlay style window:
/// topmost (re-assertable, because fullscreen games steal the top slot),
/// click-through, non-activating, hidden from Alt-Tab, translucent, and
/// pinned to whatever virtual desktop the user is currently on.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32Overlay
{
    private const int GWL_EXSTYLE = -20;

    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint LWA_COLORKEY = 0x00000001;
    private const uint LWA_ALPHA = 0x00000002;

    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const nint HTCAPTION = 2;

    private const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, out RECT rect, uint winIni);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Finds a visible top-level window of this process by exact title.</summary>
    public static nint FindProcessWindow(string title)
    {
        var pid = (uint)Environment.ProcessId;
        nint found = 0;
        var buffer = new StringBuilder(256);

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != pid || !IsWindowVisible(hWnd))
            {
                return true;
            }

            buffer.Clear();
            GetWindowText(hWnd, buffer, buffer.Capacity);
            if (buffer.ToString() == title)
            {
                found = hWnd;
                return false;
            }

            return true;
        }, 0);

        return found;
    }

    /// <summary>
    /// WS_EX_LAYERED is required for the color-key transparency (see
    /// <see cref="ApplyBlackColorKey"/>); per-window alpha (LWA_ALPHA) is never used.
    /// </summary>
    public static void ApplyOverlayStyles(nint hWnd, bool clickThrough)
    {
        var exStyle = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        if (clickThrough)
        {
            exStyle |= WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)exStyle);
    }

    /// <summary>
    /// Declares pure black (#000000) as this window's transparency key: the compositor
    /// drops every pixel that renders exactly black, revealing whatever is behind the
    /// window. The overlay's empty background reliably renders as pure black on this
    /// pipeline (the render path discards the alpha channel, leaving premultiplied
    /// black), so the keyed areas are precisely the "transparent" XAML pixels. Keyed
    /// pixels are also skipped by hit-testing, which reinforces click-through.
    /// </summary>
    public static void ApplyBlackColorKey(nint hWnd) =>
        SetLayeredWindowAttributes(hWnd, 0x00000000, 0, LWA_COLORKEY);

    // --- Panel window ---------------------------------------------------------------
    //
    // Color keying is binary, so the lyric window alone cannot show a *partially*
    // transparent background. The gradient comes from a second, content-less Win32
    // window: a rounded dark rectangle using LWA_ALPHA (true uniform blending with
    // whatever is behind), glued directly beneath the lyric window. Keyed pixels of
    // the lyric window reveal the panel; the panel's alpha is the opacity slider.

    private const string PanelClassName = "LyricSyncOverlayPanel";
    private const uint PanelColor = 0x0014100D; // COLORREF 0x00BBGGRR == #0D1014

    private const uint WS_POPUP = 0x80000000;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const uint DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWCP_DONOTROUND = 1;
    private const int PanelCornerRadius = 14; // logical px; scaled by DPI for the region

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOZORDER = 0x0004;
    private const int GCL_STYLE = -26;
    private const long CS_DROPSHADOW = 0x00020000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool redraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, uint attribute, ref int value, int size);

    private static bool _panelClassRegistered;
    private static nint _panelHwnd;
    // Region cache: keyed on the hwnds too, so regions are re-applied when the overlay is
    // hidden and re-shown (new windows, potentially the same size as before).
    private static nint _regionPanelHwnd;
    private static nint _regionOwnerHwnd;
    private static int _panelRgnW;
    private static int _panelRgnH;

    public static nint CreatePanelWindow()
    {
        if (!_panelClassRegistered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                // No managed WndProc needed — the panel is inert, DefWindowProc does it all.
                lpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                hInstance = GetModuleHandleW(null),
                hbrBackground = CreateSolidBrush(PanelColor),
                lpszClassName = PanelClassName,
            };
            _panelClassRegistered = RegisterClassExW(ref wc) != 0;
        }

        var exStyle = (uint)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST);
        var hwnd = CreateWindowExW(exStyle, PanelClassName, null, WS_POPUP,
            0, 0, 10, 10, 0, 0, GetModuleHandleW(null), 0);

        if (hwnd != 0)
        {
            // No DWM rounded corners: they come with a drop shadow that can't be separated.
            // The rounded look is provided by a window region in SyncPanelToOwner instead.
            RemoveDwmBorder(hwnd);
            SuppressShadow(hwnd);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        return hwnd;
    }

    /// <summary>
    /// Removes the thin frame border the DWM draws around a window (especially with rounded
    /// corners). That border is part of the non-client frame, not the window's content, so
    /// LWA_ALPHA never fades it — without this it lingers as a faint box outline even at 0%.
    /// </summary>
    public static void RemoveDwmBorder(nint hWnd)
    {
        var none = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    /// <summary>
    /// Removes every source of the drop shadow Windows can draw around an overlay window —
    /// including the legacy "Show shadows under windows" performance option, which varies
    /// per machine. Idempotent (safe to re-assert periodically):
    ///  1. DWM non-client rendering off (kills the standard compositor shadow).
    ///  2. Win11 corner rounding off (rounding brings its own shadow; the panel's rounded
    ///     look comes from a window region instead).
    ///  3. Frame styles stripped to a pure popup — Uno's "borderless" windows still carry
    ///     WS_CAPTION/WS_THICKFRAME under the hood, and those are what shadows attach to.
    ///  4. Legacy CS_DROPSHADOW class bit cleared (driven by the sysdm.cpl checkbox).
    /// </summary>
    public static void SuppressShadow(nint hWnd)
    {
        var policy = DWMNCRP_DISABLED;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

        var corner = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        var stripped = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        if (stripped != style)
        {
            SetWindowLongPtr(hWnd, GWL_STYLE, (nint)stripped);
            SetWindowPos(hWnd, 0, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        var classStyle = (long)GetClassLongPtrW(hWnd, GCL_STYLE);
        if ((classStyle & CS_DROPSHADOW) != 0)
        {
            SetClassLongPtrW(hWnd, GCL_STYLE, (nint)(classStyle & ~CS_DROPSHADOW));
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetClassLongPtrW(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetClassLongPtrW(nint hWnd, int index, nint value);

    /// <summary>Registers the panel so it follows the lyric window (see WM_WINDOWPOSCHANGED in the hit-test thunk).</summary>
    public static void SetPanelPair(nint panelHwnd) => _panelHwnd = panelHwnd;

    public static void DestroyPanelWindow(nint panelHwnd)
    {
        if (_panelHwnd == panelHwnd)
        {
            _panelHwnd = 0;
        }

        DestroyWindow(panelHwnd);
    }

    public static void SetPanelAlpha(nint panelHwnd, double opacity)
    {
        // At 0% hide the whole panel window: alpha 0 fades its content but the compositor
        // could still show its frame/corner; hiding removes every trace.
        if (opacity <= 0.001)
        {
            ShowWindow(panelHwnd, SW_HIDE);
            return;
        }

        ShowWindow(panelHwnd, SW_SHOWNOACTIVATE);
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 1, 255);
        SetLayeredWindowAttributes(panelHwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Aligns the panel to the lyric window's rect, directly beneath it in z-order.</summary>
    public static void SyncPanelToOwner(nint ownerHwnd)
    {
        if (_panelHwnd == 0 || ownerHwnd == 0)
        {
            return;
        }

        if (GetWindowRect(ownerHwnd, out var rect))
        {
            SetWindowPos(_panelHwnd, ownerHwnd, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);

            // Rebuild the window regions only when the size or the windows change —
            // SetWindowRgn takes ownership of each region. Two purposes:
            //   1. Rounded corners on the panel (DWM rounding is off to avoid its shadow).
            //   2. Shadow suppression: a window with ANY region set gets no DWM drop shadow,
            //      so the same region on the lyric window kills the halo around the box.
            if (rect.Width != _panelRgnW || rect.Height != _panelRgnH
                || _regionPanelHwnd != _panelHwnd || _regionOwnerHwnd != ownerHwnd)
            {
                _panelRgnW = rect.Width;
                _panelRgnH = rect.Height;
                _regionPanelHwnd = _panelHwnd;
                _regionOwnerHwnd = ownerHwnd;
                var radius = (int)Math.Round(PanelCornerRadius * GetScale(_panelHwnd)) * 2;
                SetWindowRgn(_panelHwnd, CreateRoundRectRgn(0, 0, rect.Width + 1, rect.Height + 1, radius, radius), true);
                SetWindowRgn(ownerHwnd, CreateRoundRectRgn(0, 0, rect.Width + 1, rect.Height + 1, radius, radius), true);
            }
        }
    }

    public static void SetClickThrough(nint hWnd, bool clickThrough) => ApplyOverlayStyles(hWnd, clickThrough);

    // --- Hit-test override ---------------------------------------------------------
    //
    // The window procedure is subclassed so WM_NCHITTEST can be answered directly:
    // - locked (normal) mode  → HTTRANSPARENT: clicks fall through to whatever is below
    // - move mode             → HTCAPTION: the OS itself handles dragging the window
    // This is more reliable than WS_EX_TRANSPARENT alone and gives drag for free.

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private static readonly nint HTTRANSPARENT = -1;

    private static readonly Dictionary<nint, nint> _previousWndProcs = [];
    private static WndProcDelegate? _hitTestProc; // rooted so the thunk is never collected
    private static volatile bool _moveMode;

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint prevProc, nint hWnd, uint msg, nint wParam, nint lParam);

    public static void InstallHitTestOverride(nint hWnd)
    {
        if (_previousWndProcs.ContainsKey(hWnd))
        {
            return;
        }

        _hitTestProc ??= (h, msg, wParam, lParam) =>
        {
            if (msg == WM_NCHITTEST && _previousWndProcs.ContainsKey(h))
            {
                return _moveMode ? HTCAPTION : HTTRANSPARENT;
            }

            if (msg == WM_WINDOWPOSCHANGED && _previousWndProcs.ContainsKey(h))
            {
                // Keep the panel glued to the lyric window while it is dragged or moved.
                SyncPanelToOwner(h);
            }

            return _previousWndProcs.TryGetValue(h, out var prev)
                ? CallWindowProcW(prev, h, msg, wParam, lParam)
                : 0;
        };

        var previous = SetWindowLongPtr(hWnd, GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_hitTestProc));
        _previousWndProcs[hWnd] = previous;
    }

    public static void RemoveHitTestOverride(nint hWnd)
    {
        if (_previousWndProcs.Remove(hWnd, out var previous))
        {
            SetWindowLongPtr(hWnd, GWLP_WNDPROC, previous);
        }
    }

    public static void SetMoveMode(bool moveMode) => _moveMode = moveMode;

    public static void AssertTopmost(nint hWnd) =>
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void RemoveTopmost(nint hWnd) =>
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void BeginDrag(nint hWnd)
    {
        ReleaseCapture();
        SendMessage(hWnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    public static RECT? GetRect(nint hWnd) => GetWindowRect(hWnd, out var rect) ? rect : null;

    public static void Move(nint hWnd, int x, int y, int width, int height) =>
        MoveWindow(hWnd, x, y, width, height, true);

    public static double GetScale(nint hWnd)
    {
        var dpi = GetDpiForWindow(hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>True when the given rectangle is at least partially on a connected screen.</summary>
    public static bool IsOnScreen(int x, int y, int width, int height)
    {
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        var vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vRight = vLeft + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vBottom = vTop + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        return x < vRight && x + width > vLeft && y < vBottom && y + height > vTop;
    }

    /// <summary>Places the overlay bottom-center of the primary work area, subtitle style.</summary>
    public static void MoveToDefaultPosition(nint hWnd)
    {
        if (!SystemParametersInfoW(SPI_GETWORKAREA, 0, out var workArea, 0))
        {
            return;
        }

        var scale = GetScale(hWnd);
        var width = Math.Min((int)(1000 * scale), (int)(workArea.Width * 0.72));
        var height = (int)(200 * scale);
        var x = workArea.Left + (workArea.Width - width) / 2;
        var y = workArea.Bottom - height - (int)(48 * scale);
        MoveWindow(hWnd, x, y, width, height, true);
    }

    // --- Virtual desktop pinning -------------------------------------------------
    //
    // Uses only the documented IVirtualDesktopManager COM interface: when the overlay
    // is not on the active virtual desktop, it is moved to the desktop of the current
    // foreground window. Combined with the periodic topmost re-assertion this makes
    // the overlay follow the user across virtual desktops.

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerComObject
    {
    }

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsWindowOnCurrentVirtualDesktop(nint topLevelWindow);

        Guid GetWindowDesktopId(nint topLevelWindow);

        void MoveWindowToDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);
    }

    private static IVirtualDesktopManager? _desktopManager;

    public static void EnsureOnCurrentDesktop(nint hWnd)
    {
        try
        {
            _desktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManagerComObject();

            if (_desktopManager.IsWindowOnCurrentVirtualDesktop(hWnd))
            {
                return;
            }

            var foreground = GetForegroundWindow();
            if (foreground == 0 || foreground == hWnd)
            {
                return;
            }

            var desktopId = _desktopManager.GetWindowDesktopId(foreground);
            if (desktopId == Guid.Empty)
            {
                return;
            }

            _desktopManager.MoveWindowToDesktop(hWnd, desktopId);
        }
        catch
        {
            // Virtual desktop APIs can fail transiently (e.g. during a desktop switch
            // animation, or if the shell is restarting); try again on the next tick.
            _desktopManager = null;
        }
    }
}
