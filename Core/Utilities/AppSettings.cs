using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Storage;

namespace LinuxDo.Core.Utilities;

public enum AppAppearance { System, Light, Dark }
public enum ContentFontSize { Small, Medium, Large, XLarge }
public enum ListDensity { Compact, Comfortable, Roomy }

public partial class AppSettings : ObservableObject
{
    private readonly ApplicationDataContainer _defaults = ApplicationData.Current.LocalSettings;

    public static AppSettings Current { get; } = new();

    [ObservableProperty] private AppAppearance _appearance;
    [ObservableProperty] private ContentFontSize _fontSize;
    [ObservableProperty] private ListDensity _listDensity;
    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private bool _showLocalListBadges;
    [ObservableProperty] private string _mutedKeywordsText = "";
    [ObservableProperty] private double _readingLineSpacing = 1.15;
    [ObservableProperty] private bool _collapseLongPosts;
    [ObservableProperty] private bool _watchClipboardForTopicLinks = true;
    [ObservableProperty] private bool _systemNotificationBanners = true;
    [ObservableProperty] private bool _autosaveServerDrafts = true;
    [ObservableProperty] private string _baseUrlString = "https://linux.do";

    public AppSettings()
    {
        Appearance = Enum.TryParse(Get("settings.appearance", "System"), out AppAppearance a) ? a : AppAppearance.System;
        FontSize = Enum.TryParse(Get("settings.fontSize", "Medium"), out ContentFontSize f) ? f : ContentFontSize.Medium;
        ListDensity = Enum.TryParse(Get("settings.listDensity", "Comfortable"), out ListDensity d) ? d : ListDensity.Comfortable;
        ShowTrayIcon = GetBool("settings.showTrayIcon", true);
        ShowLocalListBadges = GetBool("settings.showLocalListBadges", false);
        MutedKeywordsText = Get("settings.mutedKeywords", "");
        ReadingLineSpacing = GetDouble("settings.readingLineSpacing", 1.15);
        CollapseLongPosts = GetBool("settings.collapseLongPosts", false);
        WatchClipboardForTopicLinks = GetBool("settings.watchClipboard", true);
        SystemNotificationBanners = GetBool("settings.systemNotificationBanners", true);
        AutosaveServerDrafts = GetBool("settings.autosaveServerDrafts", true);
        BaseUrlString = Get("settings.baseURL", "https://linux.do");
    }

    public Uri BaseUrl
    {
        get
        {
            if (Uri.TryCreate(BaseUrlString.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri;
            return new Uri("https://linux.do");
        }
    }

    public IReadOnlyList<string> MutedKeywords =>
        MutedKeywordsText
            .Split([',', '，', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToList();

    public bool IsTitleMuted(string title)
    {
        var keys = MutedKeywords;
        if (keys.Count == 0) return false;
        var lower = title.ToLowerInvariant();
        return keys.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    public double BodyFontSize => FontSize switch
    {
        ContentFontSize.Small => 13,
        ContentFontSize.Large => 17,
        ContentFontSize.XLarge => 19,
        _ => 15
    };

    public double RowVerticalPadding => ListDensity switch
    {
        ListDensity.Compact => 2,
        ListDensity.Roomy => 10,
        _ => 6
    };

    public int TitleLineLimit => ListDensity switch
    {
        ListDensity.Compact => 1,
        ListDensity.Roomy => 3,
        _ => 2
    };

    partial void OnAppearanceChanged(AppAppearance value) => Set("settings.appearance", value.ToString());
    partial void OnFontSizeChanged(ContentFontSize value) => Set("settings.fontSize", value.ToString());
    partial void OnListDensityChanged(ListDensity value) => Set("settings.listDensity", value.ToString());
    partial void OnShowTrayIconChanged(bool value) => SetBool("settings.showTrayIcon", value);
    partial void OnShowLocalListBadgesChanged(bool value) => SetBool("settings.showLocalListBadges", value);
    partial void OnMutedKeywordsTextChanged(string value) => Set("settings.mutedKeywords", value);
    partial void OnReadingLineSpacingChanged(double value) => SetDouble("settings.readingLineSpacing", value);
    partial void OnCollapseLongPostsChanged(bool value) => SetBool("settings.collapseLongPosts", value);
    partial void OnWatchClipboardForTopicLinksChanged(bool value) => SetBool("settings.watchClipboard", value);
    partial void OnSystemNotificationBannersChanged(bool value) => SetBool("settings.systemNotificationBanners", value);
    partial void OnAutosaveServerDraftsChanged(bool value) => SetBool("settings.autosaveServerDrafts", value);
    partial void OnBaseUrlStringChanged(string value)
    {
        var trimmed = value.Trim();
        if (trimmed != value)
        {
            BaseUrlString = trimmed;
            return;
        }
        Set("settings.baseURL", BaseUrlString);
        _ = Services.DiscourseAPI.Shared.UpdateBaseUrlAsync(BaseUrl);
    }

    private string Get(string key, string fallback)
        => _defaults.Values.TryGetValue(key, out var v) && v is string s ? s : fallback;

    private bool GetBool(string key, bool fallback)
        => _defaults.Values.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private double GetDouble(string key, double fallback)
        => _defaults.Values.TryGetValue(key, out var v) ? Convert.ToDouble(v) : fallback;

    private void Set(string key, string value) => _defaults.Values[key] = value;
    private void SetBool(string key, bool value) => _defaults.Values[key] = value;
    private void SetDouble(string key, double value) => _defaults.Values[key] = value;
}
