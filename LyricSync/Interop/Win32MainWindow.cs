using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LyricSync.Interop;

/// <summary>
/// Win32 control over the main (control-panel) window: hide/show it from the taskbar and
/// intercept its caption buttons. The <b>minimize</b> button hides the app to the tray
/// (hiding a window removes its taskbar button — exactly "minimize to tray"); the
/// <b>close</b> (X) button quits the app entirely.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32MainWindow
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const long SC_MINIMIZE = 0xF020;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private static WndProcDelegate? _proc; // rooted
    private static nint _originalProc;
    private static nint _hookedHwnd;
    private static Action? _onMinimize; // hide to tray
    private static Action? _onClose;    // quit the app

    /// <summary>
    /// Redirects the caption buttons: minimize → <paramref name="onMinimize"/> (hide to tray),
    /// close → <paramref name="onClose"/> (quit). Both default behaviors are suppressed.
    /// </summary>
    public static void InstallHooks(nint hWnd, Action onMinimize, Action onClose)
    {
        if (hWnd == 0 || _hookedHwnd == hWnd)
        {
            return;
        }

        _hookedHwnd = hWnd;
        _onMinimize = onMinimize;
        _onClose = onClose;
        _proc = HookProc;
        _originalProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_proc));
    }

    private static nint HookProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            _onMinimize?.Invoke();
            return 0; // swallow the default minimize-to-taskbar; we hid to the tray instead
        }

        if (msg == WM_CLOSE)
        {
            _onClose?.Invoke(); // quits the process; if it returns, fall through to a real close
            return CallWindowProcW(_originalProc, hWnd, msg, wParam, lParam);
        }

        return CallWindowProcW(_originalProc, hWnd, msg, wParam, lParam);
    }

    public static void Hide(nint hWnd) => ShowWindow(hWnd, SW_HIDE);

    public static void Show(nint hWnd)
    {
        ShowWindow(hWnd, SW_SHOW);
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CallWindowProcW(nint prevProc, nint hWnd, uint msg, nint wParam, nint lParam);
}
