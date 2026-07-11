using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LinuxDo.Features.Settings;

public sealed partial class SettingsPage : Page
{
    private bool _ready;
    private AppShortcutAction? _capturing;
    private Button? _captureButton;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var s = AppSettings.Current;
        SelectTag(AppearanceBox, s.Appearance.ToString());
        SelectTag(FontSizeBox, s.FontSize.ToString());
        SelectTag(DensityBox, s.ListDensity.ToString());
        LineSpacingSlider.Value = s.ReadingLineSpacing;
        CollapseLongPostsSwitch.IsOn = s.CollapseLongPosts;
        ClipboardSwitch.IsOn = s.WatchClipboardForTopicLinks;
        LocalBadgesSwitch.IsOn = s.ShowLocalListBadges;
        NotifBannersSwitch.IsOn = s.SystemNotificationBanners;
        TrayIconSwitch.IsOn = s.ShowTrayIcon;
        AutosaveDraftsSwitch.IsOn = s.AutosaveServerDrafts;
        KeywordsBox.Text = s.MutedKeywordsText;
        BaseUrlBox.Text = s.BaseUrlString;
        BuildText.Text = $"构建 {AppVersion.BuildId}";
        LogPathText.Text = AppLog.CurrentLogFile;
        UpdateStatusText.Text = "";
        RefreshShortcutList();
        _ready = true;
    }

    private void RefreshShortcutList()
    {
        ShortcutList.ItemsSource = ShortcutStore.Current.AllItems();
        ShortcutHintText.Text = "";
        _capturing = null;
        _captureButton = null;
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void AppearanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AppearanceBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<AppAppearance>(item.Tag?.ToString(), out var v))
        {
            AppSettings.Current.Appearance = v;
            ApplyTheme(v);
            StatusText.Text = "外观已更新";
        }
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FontSizeBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<ContentFontSize>(item.Tag?.ToString(), out var v))
            AppSettings.Current.FontSize = v;
    }

    private void DensityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || DensityBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<ListDensity>(item.Tag?.ToString(), out var v))
            AppSettings.Current.ListDensity = v;
    }

    private void LineSpacingSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.ReadingLineSpacing = e.NewValue;
    }

    private void CollapseLongPostsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.CollapseLongPosts = CollapseLongPostsSwitch.IsOn;
    }

    private void ClipboardSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.WatchClipboardForTopicLinks = ClipboardSwitch.IsOn;
    }

    private void LocalBadgesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.ShowLocalListBadges = LocalBadgesSwitch.IsOn;
    }

    private void NotifBannersSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.SystemNotificationBanners = NotifBannersSwitch.IsOn;
    }

    private void TrayIconSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.ShowTrayIcon = TrayIconSwitch.IsOn;
        try
        {
            if (TrayIconSwitch.IsOn)
            {
                TrayIconService.Shared.Configure(App.WindowHandle);
                TrayIconService.Shared.Refresh(UserSessionStore.Current.UnreadCount);
                StatusText.Text = "托盘图标已启用（始终显示；关闭窗口会最小化到托盘）";
            }
            else
            {
                TrayIconService.Shared.TearDown();
                StatusText.Text = "托盘图标已关闭（关闭窗口将无法从托盘恢复，建议保持开启）";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "托盘： " + ex.Message;
        }
    }

    private void AutosaveDraftsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.AutosaveServerDrafts = AutosaveDraftsSwitch.IsOn;
    }

    private void KeywordsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.MutedKeywordsText = KeywordsBox.Text ?? "";
    }

    private void BaseUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_ready) AppSettings.Current.BaseUrlString = BaseUrlBox.Text ?? "https://linux.do";
    }

    private void Challenge_Click(object sender, RoutedEventArgs e)
    {
        SiteAccessStore.Current.PresentChallenge(force: true);
    }

    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppLog.LogDirectory;
            LogPathText.Text = AppLog.CurrentLogFile;
            await Launcher.LaunchFolderPathAsync(dir);
            StatusText.Text = "已打开日志目录";
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开日志目录：" + ex.Message;
            LogPathText.Text = AppLog.CurrentLogFile;
        }
    }

    private async void CopyLogPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = AppLog.CurrentLogFile;
            LogPathText.Text = path;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            StatusText.Text = "日志路径已复制";
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        HtmlText.ClearCache();
        ImageLoader.Shared.ClearAll();
        OneboxService.Shared.ClearCache();
        PostContentCache.Clear();
        ApiResponseCache.Clear();
        var usage = ImageLoader.Shared.Usage();
        StatusText.Text = $"缓存已清理（图片内存 {usage.MemoryEntries} / 磁盘 {usage.DiskFiles}，API/帖子缓存已清空）";
    }

    private async void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(AppVersion.RepoUrl));
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "无法打开仓库：" + ex.Message;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateRing.Visibility = Visibility.Visible;
        UpdateRing.IsActive = true;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var result = await UpdateService.CheckAsync();
            switch (result.ResultKind)
            {
                case UpdateCheckResult.Kind.Available:
                    UpdateStatusText.Text =
                        $"发现新构建：{result.LatestBuildId}（当前 {result.CurrentBuildId}）";
                    var go = await new ContentDialog
                    {
                        Title = "发现新版本",
                        Content = $"当前构建：{result.CurrentBuildId}\n最新构建：{result.LatestBuildId}\n\n是否前往 GitHub 下载更新？",
                        PrimaryButtonText = "更新",
                        CloseButtonText = "稍后",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = XamlRoot
                    }.ShowAsync();
                    if (go == ContentDialogResult.Primary &&
                        !string.IsNullOrEmpty(result.ReleaseUrl) &&
                        Uri.TryCreate(result.ReleaseUrl, UriKind.Absolute, out var uri))
                    {
                        await Launcher.LaunchUriAsync(uri);
                    }
                    break;
                case UpdateCheckResult.Kind.UpToDate:
                    UpdateStatusText.Text = $"已是最新（构建 {result.CurrentBuildId}）";
                    break;
                default:
                    UpdateStatusText.Text = result.Message ?? "检查失败";
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            UpdateRing.IsActive = false;
            UpdateRing.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShortcutCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AppShortcutAction action) return;
        _capturing = action;
        _captureButton = btn;
        btn.Content = "按下组合键…";
        ShortcutHintText.Text = $"正在录制「{action.Title()}」— 请按下 Ctrl/Alt/Shift + 键";
        btn.Focus(FocusState.Programmatic);
        btn.KeyDown -= CaptureButton_KeyDown;
        btn.KeyDown += CaptureButton_KeyDown;
    }

    private void CaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_capturing is null) return;
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows
            or VirtualKey.RightWindows or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;

        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                   & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        // Require at least one modifier for safety
        if (!ctrl && !shift && !alt)
        {
            ShortcutHintText.Text = "请配合 Ctrl / Alt / Shift 使用";
            e.Handled = true;
            return;
        }

        uint mods = 0;
        if (ctrl) mods |= ShortcutKey.ModControl;
        if (shift) mods |= ShortcutKey.ModShift;
        if (alt) mods |= ShortcutKey.ModAlt;

        var keyStr = ShortcutKey.VirtualKeyToKey(e.Key);
        if (string.IsNullOrEmpty(keyStr) || ShortcutKey.KeyToVirtualKey(keyStr) == VirtualKey.None)
        {
            ShortcutHintText.Text = "不支持的按键";
            e.Handled = true;
            return;
        }

        var binding = new ShortcutKey(keyStr, mods);
        ShortcutStore.Current.SetKey(_capturing.Value, binding);
        ShortcutHintText.Text = $"已设置：{binding.Display}";
        StatusText.Text = $"{_capturing.Value.Title()} → {binding.Display}";

        if (_captureButton is not null)
            _captureButton.KeyDown -= CaptureButton_KeyDown;
        _capturing = null;
        _captureButton = null;
        e.Handled = true;
        RefreshShortcutList();
    }

    private void ShortcutReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AppShortcutAction action) return;
        ShortcutStore.Current.Reset(action);
        StatusText.Text = $"已重置：{action.Title()}";
        RefreshShortcutList();
    }

    private void ShortcutResetAll_Click(object sender, RoutedEventArgs e)
    {
        ShortcutStore.Current.ResetAll();
        StatusText.Text = "全部快捷键已恢复默认";
        RefreshShortcutList();
    }

    public static void ApplyTheme(AppAppearance appearance)
    {
        if (App.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = appearance switch
            {
                AppAppearance.Light => ElementTheme.Light,
                AppAppearance.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }
}
