using LinuxDo.Core.Utilities;
using LinuxDo.Features.Topic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace LinuxDo.Core.Services;

/// <summary>Opens topic detail pages in dedicated secondary windows.</summary>
public static class TopicWindowService
{
    private static readonly List<Window> OpenWindows = [];

    public static void OpenTopic(int id, string? title = null, int? postNumber = null)
    {
        try
        {
            var window = new Window
            {
                Title = string.IsNullOrEmpty(title) ? $"主题 #{id}" : title!
            };

            var root = new Frame();
            window.Content = root;
            root.Navigate(typeof(TopicDetailPage), AppRoute.Topic(id, title, postNumber));

            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var id_win = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(id_win);
                appWindow.Resize(new SizeInt32(960, 800));
                if (!string.IsNullOrEmpty(title))
                    appWindow.Title = title!;
            }
            catch
            {
                // size is best-effort
            }

            // Apply current theme
            if (window.Content is FrameworkElement fe)
            {
                fe.RequestedTheme = AppSettings.Current.Appearance switch
                {
                    AppAppearance.Light => ElementTheme.Light,
                    AppAppearance.Dark => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            window.Closed += (_, _) => OpenWindows.Remove(window);
            OpenWindows.Add(window);
            window.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warning("window", "Open topic window failed: " + ex.Message);
            // Fallback: open in main router
            AppRouter.Current.OpenTopic(id, title, postNumber);
        }
    }
}
