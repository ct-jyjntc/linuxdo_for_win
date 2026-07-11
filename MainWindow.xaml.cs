using System.Runtime.InteropServices;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace LinuxDo;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Multi-pane nav + content: ~1200×800 DIPs (matches mac min size)
        nint hwnd;
        try
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(820 * scale)));
        }
        catch
        {
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppWindow.Resize(new SizeInt32(1240, 820));
        }

        RootFrame.Navigate(typeof(MainPage));

        // Tray icon (optional)
        try
        {
            if (AppSettings.Current.ShowTrayIcon)
                TrayIconService.Shared.Configure(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warning("tray", ex.Message);
        }

        Closed += (_, _) =>
        {
            try { TrayIconService.Shared.Dispose(); } catch { /* ignore */ }
        };
    }
}
