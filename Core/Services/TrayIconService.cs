using System.Runtime.InteropServices;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;

namespace LinuxDo.Core.Services;

/// <summary>System tray icon with unread badge + context menu (Win32 NOTIFYICON).</summary>
public sealed class TrayIconService : IDisposable
{
    public static TrayIconService Shared { get; } = new();

    private const int WmTrayicon = 0x8001;
    private const int WmLbuttondblclk = 0x0203;
    private const int WmRbuttonup = 0x0205;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifShowTip = 0x00000080;

    private bool _added;
    private nint _hwnd;
    private nint _icon;
    private int _unread;
    private SubclassProc? _subclassProc;
    private bool _disposed;

    public void Configure(nint hwnd)
    {
        if (!AppSettings.Current.ShowTrayIcon) return;
        _hwnd = hwnd;
        _icon = LoadIcon();
        _subclassProc = WndProc;
        SetWindowSubclass(hwnd, _subclassProc, 1, 0);
        AddOrModify(NimAdd);
        _added = true;
        Refresh(UserSessionStore.Current.UnreadCount);
        UserSessionStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UserSessionStore.UnreadCount) or nameof(UserSessionStore.CurrentUser))
                App.DispatcherQueue?.TryEnqueue(() => Refresh(UserSessionStore.Current.UnreadCount));
        };
    }

    public void Refresh(int unread)
    {
        if (!_added || _hwnd == 0) return;
        _unread = unread;
        AddOrModify(NimModify);
    }

    public void TearDown()
    {
        if (!_added) return;
        var data = MakeData();
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
        if (_hwnd != 0 && _subclassProc is not null)
            RemoveWindowSubclass(_hwnd, _subclassProc, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        TearDown();
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
        _disposed = true;
    }

    private void AddOrModify(int message)
    {
        var data = MakeData();
        Shell_NotifyIcon(message, ref data);
    }

    private NotifyIconData MakeData()
    {
        var tip = _unread > 0 ? $"LinuxDo · {_unread} 条未读通知" : "LinuxDo";
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = WmTrayicon,
            hIcon = _icon,
            szTip = tip.Length > 127 ? tip[..127] : tip
        };
    }

    private nint LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(0, path, 1, 16, 16, 0x00000010);
                if (h != 0) return h;
            }
        }
        catch { /* fallthrough */ }
        return LoadIcon(0, (nint)32512); // IDI_APPLICATION
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nint uid, nint data)
    {
        if (msg == WmTrayicon)
        {
            var mouse = (int)(lParam & 0xFFFF);
            if (mouse == WmLbuttondblclk || mouse == 0x0202) // LBUTTONUP
            {
                App.DispatcherQueue?.TryEnqueue(ShowMainWindow);
            }
            else if (mouse == WmRbuttonup)
            {
                App.DispatcherQueue?.TryEnqueue(ShowContextMenu);
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowMainWindow()
    {
        try
        {
            App.Window?.Activate();
            var hwnd = App.WindowHandle;
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        catch { /* ignore */ }
    }

    private void ShowContextMenu()
    {
        try
        {
            var menu = CreatePopupMenu();
            AppendMenu(menu, 0, 1, "打开 LinuxDo");
            AppendMenu(menu, 0, 2, "未读");
            AppendMenu(menu, 0, 3, "通知");
            AppendMenu(menu, 0, 4, "稍后阅读");
            AppendMenu(menu, 0x800, 0, ""); // separator
            AppendMenu(menu, 0, 5, "刷新角标");
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 6, "退出");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(menu, 0x0100 /*TPM_RETURNCMD*/, pt.X, pt.Y, 0, _hwnd, 0);
            DestroyMenu(menu);

            switch (cmd)
            {
                case 1:
                    ShowMainWindow();
                    AppRouter.Current.SelectRoot(AppRoute.Latest);
                    break;
                case 2:
                    ShowMainWindow();
                    if (UserSessionStore.Current.RequireLogin())
                        AppRouter.Current.SelectRoot(AppRoute.Unread);
                    break;
                case 3:
                    ShowMainWindow();
                    if (UserSessionStore.Current.RequireLogin())
                        AppRouter.Current.SelectRoot(AppRoute.Notifications);
                    break;
                case 4:
                    ShowMainWindow();
                    AppRouter.Current.SelectRoot(AppRoute.ReadLater);
                    break;
                case 5:
                    _ = UserSessionStore.Current.RefreshCurrentUserAsync();
                    break;
                case 6:
                    TearDown();
                    Application.Current.Exit();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", ex.Message);
        }
    }

    // ── P/Invoke ───────────────────────────────────────────

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint uid, nint data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfn, nint uid, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfn, nint uid);

    [DllImport("user32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

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
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prc);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);

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
}
