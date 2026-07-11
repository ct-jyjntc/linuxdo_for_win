using System.Runtime.InteropServices;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;

namespace LinuxDo.Core.Services;

/// <summary>
/// System tray icon (notification area) with left-click restore + right-click menu.
/// Uses a dedicated message-only HWND so WinUI main-window subclass issues do not break clicks.
/// Icon stays for the whole app lifetime (not only after close-to-tray).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public static TrayIconService Shared { get; } = new();

    private const int WmTrayIcon = 0x8001;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmCommand = 0x0111;
    private const int WmDestroy = 0x0002;
    private const int WmNull = 0x0000;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifShowTip = 0x00000080;

    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonNotify = 0x0080;

    private const int IdOpen = 1001;
    private const int IdUnread = 1002;
    private const int IdNotifications = 1003;
    private const int IdReadLater = 1004;
    private const int IdRefresh = 1005;
    private const int IdExit = 1006;

    private bool _added;
    private bool _disposed;
    private bool _subscribedUnread;
    private nint _msgHwnd;
    private nint _mainHwnd;
    private nint _icon;
    private int _unread;
    private WndProc? _wndProc; // keep alive
    private DateTime _lastLeftClickUtc = DateTime.MinValue;

    /// <summary>Create / refresh tray icon. Safe to call multiple times.</summary>
    public void Configure(nint mainWindowHwnd)
    {
        if (_disposed) _disposed = false;
        if (!AppSettings.Current.ShowTrayIcon)
        {
            TearDown();
            return;
        }

        _mainHwnd = mainWindowHwnd;
        if (_icon == 0) _icon = LoadIconHandle();

        EnsureMessageWindow();
        if (_msgHwnd == 0)
        {
            AppLog.Warning("tray", "message window create failed");
            return;
        }

        if (!_added)
        {
            if (!ShellNotify(NimAdd))
            {
                // Retry once after small delay (shell not ready)
                Thread.Sleep(50);
                ShellNotify(NimAdd);
            }
            _added = true;
        }
        else
        {
            ShellNotify(NimModify);
        }

        Refresh(UserSessionStore.Current.UnreadCount);
        SubscribeUnread();
    }

    public void Refresh(int unread)
    {
        if (!_added || _msgHwnd == 0) return;
        _unread = unread;
        ShellNotify(NimModify);
    }

    public void TearDown()
    {
        if (_added && _msgHwnd != 0)
        {
            try { ShellNotify(NimDelete); } catch { /* ignore */ }
            _added = false;
        }
        DestroyMessageWindow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        TearDown();
        if (_icon != 0)
        {
            try { DestroyIcon(_icon); } catch { /* ignore */ }
            _icon = 0;
        }
        _disposed = true;
    }

    public static void ShowMainWindow()
    {
        try
        {
            MainWindow.RestoreFromTray();
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", "ShowMainWindow: " + ex.Message);
            try
            {
                App.Window?.Activate();
                ShowWindow(App.WindowHandle, 9);
                SetForegroundWindow(App.WindowHandle);
            }
            catch { /* ignore */ }
        }
    }

    private void SubscribeUnread()
    {
        if (_subscribedUnread) return;
        _subscribedUnread = true;
        UserSessionStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UserSessionStore.UnreadCount) or nameof(UserSessionStore.CurrentUser))
                App.DispatcherQueue?.TryEnqueue(() => Refresh(UserSessionStore.Current.UnreadCount));
        };
    }

    private void EnsureMessageWindow()
    {
        if (_msgHwnd != 0) return;

        _wndProc = MessageWndProc;
        var className = "LinuxDoTrayMsgWnd_" + Guid.NewGuid().ToString("N")[..8];
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // Already registered is fine in rare re-entry
            if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                AppLog.Warning("tray", "RegisterClassEx failed: " + err);
                return;
            }
        }

        // HWND_MESSAGE = -3 → message-only window
        _msgHwnd = CreateWindowEx(
            0, className, "LinuxDoTray",
            0, 0, 0, 0, 0,
            new nint(-3), 0, GetModuleHandle(null), 0);

        if (_msgHwnd == 0)
            AppLog.Warning("tray", "CreateWindowEx failed: " + Marshal.GetLastWin32Error());
    }

    private void DestroyMessageWindow()
    {
        if (_msgHwnd == 0) return;
        try { DestroyWindow(_msgHwnd); } catch { /* ignore */ }
        _msgHwnd = 0;
    }

    private nint MessageWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmTrayIcon)
        {
            // Low word of lParam is the mouse message
            var mouse = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouse)
            {
                case WmLButtonUp:
                    // Debounce double-fire from shell
                    if ((DateTime.UtcNow - _lastLeftClickUtc).TotalMilliseconds < 250)
                        break;
                    _lastLeftClickUtc = DateTime.UtcNow;
                    App.DispatcherQueue?.TryEnqueue(ShowMainWindow);
                    break;
                case WmLButtonDblClk:
                    _lastLeftClickUtc = DateTime.UtcNow;
                    App.DispatcherQueue?.TryEnqueue(ShowMainWindow);
                    break;
                case WmRButtonUp:
                case WmContextMenu:
                    App.DispatcherQueue?.TryEnqueue(ShowContextMenu);
                    break;
            }
            return 0;
        }

        if (msg == WmCommand)
        {
            // Fallback if menu posts WM_COMMAND
            var id = (int)(wParam.ToInt64() & 0xFFFF);
            App.DispatcherQueue?.TryEnqueue(() => HandleMenuCommand(id));
            return 0;
        }

        if (msg == WmDestroy)
            return 0;

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        try
        {
            var menu = CreatePopupMenu();
            if (menu == 0) return;

            AppendMenu(menu, 0, IdOpen, "打开 LinuxDo");
            AppendMenu(menu, 0, IdUnread, "未读");
            AppendMenu(menu, 0, IdNotifications, "通知");
            AppendMenu(menu, 0, IdReadLater, "稍后阅读");
            AppendMenu(menu, 0x800, 0, string.Empty); // MF_SEPARATOR
            AppendMenu(menu, 0, IdRefresh, "刷新角标");
            AppendMenu(menu, 0x800, 0, string.Empty);
            AppendMenu(menu, 0, IdExit, "退出");

            GetCursorPos(out var pt);
            // Required dance so menu dismisses correctly and TrackPopupMenu works
            var fg = _msgHwnd != 0 ? _msgHwnd : _mainHwnd;
            SetForegroundWindow(fg);

            var cmd = (int)TrackPopupMenuEx(
                menu,
                TpmLeftAlign | TpmRightButton | TpmReturnCmd | TpmNonNotify,
                pt.X, pt.Y,
                fg,
                0);

            // Required after TrackPopupMenu so menu closes properly
            PostMessage(fg, WmNull, 0, 0);
            DestroyMenu(menu);

            if (cmd != 0)
                HandleMenuCommand(cmd);
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", "menu: " + ex.Message);
        }
    }

    private void HandleMenuCommand(int cmd)
    {
        try
        {
            switch (cmd)
            {
                case IdOpen:
                    ShowMainWindow();
                    break;
                case IdUnread:
                    ShowMainWindow();
                    if (UserSessionStore.Current.RequireLogin())
                        AppRouter.Current.SelectRoot(AppRoute.Unread);
                    break;
                case IdNotifications:
                    ShowMainWindow();
                    if (UserSessionStore.Current.RequireLogin())
                        AppRouter.Current.SelectRoot(AppRoute.Notifications);
                    break;
                case IdReadLater:
                    ShowMainWindow();
                    AppRouter.Current.SelectRoot(AppRoute.ReadLater);
                    break;
                case IdRefresh:
                    _ = UserSessionStore.Current.RefreshCurrentUserAsync();
                    break;
                case IdExit:
                    MainWindow.RequestExit();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", "command: " + ex.Message);
        }
    }

    private bool ShellNotify(int message)
    {
        var data = MakeData();
        return Shell_NotifyIcon(message, ref data);
    }

    private NotifyIconData MakeData()
    {
        var tip = _unread > 0 ? $"LinuxDo · {_unread} 条未读" : "LinuxDo";
        if (tip.Length > 127) tip = tip[..127];
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _msgHwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = _icon,
            szTip = tip
        };
    }

    private nint LoadIconHandle()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(0, path, 1 /*IMAGE_ICON*/, 0, 0, 0x00000010 /*LR_LOADFROMFILE*/);
                if (h != 0) return h;
            }
        }
        catch { /* fallthrough */ }
        return LoadIcon(0, (nint)32512); // IDI_APPLICATION
    }

    // ── P/Invoke ───────────────────────────────────────────

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
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
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);
}
