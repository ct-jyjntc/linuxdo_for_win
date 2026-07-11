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
                TrayIconService.Shared.Configure(App.WindowHandle);
            else
                TrayIconService.Shared.TearDown();
            StatusText.Text = TrayIconSwitch.IsOn ? "托盘图标已启用" : "托盘图标已关闭";
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

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        HtmlText.ClearCache();
        ImageLoader.Shared.ClearAll();
        OneboxService.Shared.ClearCache();
        PostContentCache.Clear();
        var usage = ImageLoader.Shared.Usage();
        StatusText.Text = $"缓存已清理（图片内存 {usage.MemoryEntries} / 磁盘 {usage.DiskFiles}，帖子解析缓存已清空）";
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
