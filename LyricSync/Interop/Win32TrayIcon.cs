using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LyricSync.Interop;

/// <summary>
/// A Windows notification-area (system tray) icon with a right-click menu, implemented
/// directly on Win32 (Uno/WinUI has no tray API). It owns a hidden message-only window
/// that receives the icon's callbacks; that window is created on the UI thread, so its
/// messages are pumped by the app's existing message loop and the handlers run on the
/// UI thread (safe to touch XAML from the raised events).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32TrayIcon : IDisposable
{
    private const string WindowClassName = "LyricSyncTrayMsgWindow";
    private const uint CallbackMessage = 0x0400 + 1; // WM_APP + 1

    private const int IdSettings = 1;
    private const int IdToggleOverlay = 2;
    private const int IdExit = 3;

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;

    private const uint WM_COMMAND = 0x0111;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_DESTROY = 0x0002;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private static readonly nint HWND_MESSAGE = -3;
    private const int IDI_APPLICATION = 32512;

    // Raised (on the UI thread) for the tray interactions.
    public event Action? ShowSettings;
    public event Action? ToggleOverlay;
    public event Action? Exit;

    /// <summary>Supplies the current overlay visibility so the menu can label its toggle.</summary>
    public Func<bool>? IsOverlayShown { get; set; }

    private readonly WndProcDelegate _wndProc; // rooted so the native thunk stays alive
    private nint _hwnd;
    private nint _icon;
    private bool _added;

    public Win32TrayIcon()
    {
        _wndProc = WndProc;
    }

    public void Create(string tooltip)
    {
        var hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        RegisterClassExW(ref wc); // ignore "already registered" on re-create

        _hwnd = CreateWindowExW(0, WindowClassName, string.Empty, 0, 0, 0, 0, 0,
            HWND_MESSAGE, 0, hInstance, 0);
        if (_hwnd == 0)
        {
            return;
        }

        _icon = LoadAppIcon();

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = tooltip;
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>One-shot balloon tip (used once, to explain the app is still running in the tray).</summary>
    public void ShowInfo(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        var data = NewData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = message;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case CallbackMessage:
                var mouse = (uint)(lParam & 0xFFFF);
                if (mouse == WM_LBUTTONDBLCLK)
                {
                    ShowSettings?.Invoke();
                }
                else if (mouse == WM_RBUTTONUP)
                {
                    ShowMenu();
                }

                return 0;

            case WM_COMMAND:
                switch ((int)(wParam & 0xFFFF))
                {
                    case IdSettings: ShowSettings?.Invoke(); break;
                    case IdToggleOverlay: ToggleOverlay?.Invoke(); break;
                    case IdExit: Exit?.Invoke(); break;
                }

                return 0;

            case WM_DESTROY:
                return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MF_STRING, IdSettings, "Settings");
            SetMenuDefaultItem(menu, IdSettings, 0);
            var shown = IsOverlayShown?.Invoke() ?? false;
            AppendMenuW(menu, MF_STRING, IdToggleOverlay, shown ? "Hide overlay" : "Show overlay");
            AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(menu, MF_STRING, IdExit, "Exit LyricSync");

            GetCursorPos(out var pt);

            // Required so the menu dismisses when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);

            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, 0);
            PostMessageW(_hwnd, 0x0000 /* WM_NULL */, 0, 0);

            if (cmd != 0)
            {
                switch (cmd)
                {
                    case IdSettings: ShowSettings?.Invoke(); break;
                    case IdToggleOverlay: ToggleOverlay?.Invoke(); break;
                    case IdExit: Exit?.Invoke(); break;
                }
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static nint LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = ExtractIconW(GetModuleHandleW(null), exe, 0);
                // ExtractIcon returns 1 when the file has no icons; 0 on error.
                if (icon != 0 && icon != 1)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // fall through to the stock icon
        }

        return LoadIconW(0, IDI_APPLICATION);
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
    };

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    // --- Win32 interop ------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint hInst, string exeFileName, uint iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, int lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint flags, nint idNewItem, string newItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(nint hMenu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint flags, int x, int y, int reserved, nint hWnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
