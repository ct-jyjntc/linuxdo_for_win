using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Storage;

namespace LinuxDo.Core.Utilities;

public enum AppAppearance { System, Light, Dark }
public enum ContentFontSize { Small, Medium, Large, XLarge }
public enum ListDensity { Compact, Comfortable, Roomy }

/// <summary>
/// App preferences. Prefer ApplicationData.LocalSettings when available (MSIX);
/// fall back to a JSON file under LocalAppData for unpackaged / dev runs where
/// ApplicationData.Current throws (TypeInitializationException).
/// </summary>
public partial class AppSettings : ObservableObject
{
    public static AppSettings Current { get; } = CreateSafe();

    private readonly ApplicationDataContainer? _container;
    private readonly Dictionary<string, object?> _fileValues;
    private readonly string? _filePath;
    private readonly object _fileGate = new();
    private bool _useFileStore;

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

    private static AppSettings CreateSafe()
    {
        try
        {
            return new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warning("settings", "AppSettings init failed, using defaults: " + ex.Message);
            return new AppSettings(forceFileStore: true);
        }
    }

    public AppSettings() : this(forceFileStore: false) { }

    private AppSettings(bool forceFileStore)
    {
        _fileValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinuxDo", "settings.json");

        if (!forceFileStore)
        {
            try
            {
                // May throw TypeInitializationException / COMException when unpackaged
                // without proper identity, or when ApplicationData is unavailable.
                _container = ApplicationData.Current.LocalSettings;
                _useFileStore = false;
            }
            catch (Exception ex)
            {
                AppLog.Warning("settings", "ApplicationData unavailable, file store: " + ex.Message);
                _container = null;
                _useFileStore = true;
            }
        }
        else
        {
            _container = null;
            _useFileStore = true;
        }

        if (_useFileStore)
            LoadFileStore();

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
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                if (_container.Values.TryGetValue(key, out var v) && v is string s)
                    return s;
                return fallback;
            }
            lock (_fileGate)
            {
                if (_fileValues.TryGetValue(key, out var v) && v is string s)
                    return s;
                if (_fileValues.TryGetValue(key, out var any) && any is not null)
                    return Convert.ToString(any) ?? fallback;
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
        }
        return fallback;
    }

    private bool GetBool(string key, bool fallback)
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                if (_container.Values.TryGetValue(key, out var v) && v is bool b)
                    return b;
                return fallback;
            }
            lock (_fileGate)
            {
                if (_fileValues.TryGetValue(key, out var v))
                {
                    if (v is bool b) return b;
                    if (v is JsonElement je && (je.ValueKind is JsonValueKind.True or JsonValueKind.False))
                        return je.GetBoolean();
                    if (bool.TryParse(Convert.ToString(v), out var parsed)) return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
        }
        return fallback;
    }

    private double GetDouble(string key, double fallback)
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                if (_container.Values.TryGetValue(key, out var v))
                    return Convert.ToDouble(v);
                return fallback;
            }
            lock (_fileGate)
            {
                if (_fileValues.TryGetValue(key, out var v) && v is not null)
                {
                    if (v is double d) return d;
                    if (v is JsonElement je && je.ValueKind == JsonValueKind.Number)
                        return je.GetDouble();
                    if (double.TryParse(Convert.ToString(v), out var parsed)) return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
        }
        return fallback;
    }

    private void Set(string key, string value)
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                _container.Values[key] = value;
                return;
            }
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
    }

    private void SetBool(string key, bool value)
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                _container.Values[key] = value;
                return;
            }
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
    }

    private void SetDouble(string key, double value)
    {
        try
        {
            if (!_useFileStore && _container is not null)
            {
                _container.Values[key] = value;
                return;
            }
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
        catch (Exception ex)
        {
            SwitchToFileStore(ex);
            lock (_fileGate)
            {
                _fileValues[key] = value;
                SaveFileStore();
            }
        }
    }

    private void SwitchToFileStore(Exception ex)
    {
        if (_useFileStore) return;
        _useFileStore = true;
        AppLog.Warning("settings", "Falling back to file settings: " + ex.Message);
        LoadFileStore();
    }

    private void LoadFileStore()
    {
        try
        {
            if (_filePath is null) return;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict is null) return;
            lock (_fileGate)
            {
                _fileValues.Clear();
                foreach (var (k, el) in dict)
                {
                    _fileValues[k] = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                        JsonValueKind.Null => null,
                        _ => el.GetRawText()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("settings", "Load file settings: " + ex.Message);
        }
    }

    private void SaveFileStore()
    {
        try
        {
            if (_filePath is null) return;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            Dictionary<string, object?> snap;
            lock (_fileGate)
                snap = new Dictionary<string, object?>(_fileValues);
            var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            AppLog.Warning("settings", "Save file settings: " + ex.Message);
        }
    }
}
