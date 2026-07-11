using System.Runtime.InteropServices;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace LinuxDo;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int SwShowNa = 8;

    /// <summary>When true, the next close really exits the process (tray → 退出).</summary>
    private static bool _allowExit;

    private nint _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TrySetWindowIcon();

        try
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(_hwnd) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(820 * scale)));
        }
        catch
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppWindow.Resize(new SizeInt32(1240, 820));
        }

        RootFrame.Navigate(typeof(MainPage));

        // Tray icon for the whole app lifetime (not only after close)
        EnsureTray();

        // X / Alt+F4 → hide to tray (not quit)
        AppWindow.Closing += AppWindow_Closing;

        Closed += (_, _) =>
        {
            try { TrayIconService.Shared.Dispose(); } catch { /* ignore */ }
        };

        // Re-assert tray after first frame (shell sometimes drops early NIM_ADD)
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EnsureTray();
        });
    }

    private void EnsureTray()
    {
        try
        {
            // Force tray on by default for reliable close-to-tray + always-visible icon
            if (!AppSettings.Current.ShowTrayIcon)
                AppSettings.Current.ShowTrayIcon = true;

            if (_hwnd == 0)
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            TrayIconService.Shared.Configure(_hwnd);
            TrayIconService.Shared.Refresh(UserSessionStore.Current.UnreadCount);
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", ex.Message);
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit) return;

        args.Cancel = true;
        MinimizeToTray();
    }

    /// <summary>Hide main window; tray remains so user can restore.</summary>
    public void MinimizeToTray()
    {
        try
        {
            EnsureTray();

            try
            {
                AppWindow.Hide();
            }
            catch
            {
                ShowWindow(_hwnd != 0 ? _hwnd : App.WindowHandle, SwHide);
            }

            TrayIconService.Shared.Refresh(UserSessionStore.Current.UnreadCount);
        }
        catch (Exception ex)
        {
            AppLog.Warning("window", "MinimizeToTray: " + ex.Message);
        }
    }

    /// <summary>Restore and focus the main window (tray click / toast).</summary>
    public static void RestoreFromTray()
    {
        try
        {
            if (App.Window is not MainWindow win) return;
            var hwnd = win._hwnd != 0
                ? win._hwnd
                : WinRT.Interop.WindowNative.GetWindowHandle(win);

            // Show via AppWindow first
            try
            {
                if (!win.AppWindow.IsVisible)
                    win.AppWindow.Show();
            }
            catch
            {
                ShowWindow(hwnd, SwRestore);
            }

            // If minimized, restore
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            else
                ShowWindow(hwnd, SwShow);

            win.Activate();
            SetForegroundWindow(hwnd);

            // Keep tray alive
            try { TrayIconService.Shared.Configure(hwnd); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            AppLog.Warning("window", "RestoreFromTray: " + ex.Message);
        }
    }

    /// <summary>Called from tray menu「退出」— allow real close.</summary>
    public static void RequestExit()
    {
        _allowExit = true;
        try { TrayIconService.Shared.Dispose(); } catch { /* ignore */ }
        try
        {
            Application.Current.Exit();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                Path.Combine(AppContext.BaseDirectory, "AppIcon.ico"),
                Path.Combine(Environment.CurrentDirectory, "Assets", "AppIcon.ico")
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                AppWindow.SetIcon(path);
                return;
            }
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }
        catch (Exception ex)
        {
            AppLog.Warning("window", "SetIcon: " + ex.Message);
        }
    }
}
