using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;

namespace LinuxDo.Features.Home;

public enum HomeMode { Latest, Top, Newest }

public enum TopPeriod
{
    Daily, Weekly, Monthly, Yearly, All
}

public static class TopPeriodExtensions
{
    public static string Title(this TopPeriod p) => p switch
    {
        TopPeriod.Daily => "今日",
        TopPeriod.Weekly => "本周",
        TopPeriod.Monthly => "本月",
        TopPeriod.Yearly => "今年",
        TopPeriod.All => "全部",
        _ => "本周"
    };

    public static string ApiValue(this TopPeriod p) => p switch
    {
        TopPeriod.Daily => "daily",
        TopPeriod.Weekly => "weekly",
        TopPeriod.Monthly => "monthly",
        TopPeriod.Yearly => "yearly",
        TopPeriod.All => "all",
        _ => "weekly"
    };
}

public partial class TopicListItemViewModel : ObservableObject
{
    public DiscourseTopic Topic { get; }
    public DiscourseUser? Author { get; }
    public DiscourseCategory? Category { get; }
    public Uri BaseUrl { get; }

    public TopicListItemViewModel(DiscourseTopic topic, DiscourseUser? author, DiscourseCategory? category, Uri baseUrl)
    {
        Topic = topic;
        Author = author;
        Category = category;
        BaseUrl = baseUrl;
    }

    public int Id => Topic.Id;
    public string DisplayTitle => Topic.DisplayTitle;
    public string? AuthorName => Author?.Username;
    public string? AvatarUrl => Author?.AvatarUrl(BaseUrl, 80)?.AbsoluteUri;
    public string? CategoryName => Category?.Name;
    public string? CategoryColor => Category?.Color is null ? null : "#" + Category.Color;
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (AuthorName is not null) parts.Add(AuthorName);
            parts.Add($"💬 {Topic.PostsCount ?? Topic.ReplyCount ?? 0}");
            if (AppSettings.Current.ListDensity != ListDensity.Compact && Topic.Views is not null)
                parts.Add($"👁 {Topic.Views}");
            if (Topic.LikeCount is > 0) parts.Add($"❤ {Topic.LikeCount}");
            var date = Topic.BumpedDate ?? Topic.LastPostedDate ?? Topic.CreatedDate;
            if (date is not null) parts.Add(RelativeDate.Describe(date));
            return string.Join(" · ", parts);
        }
    }
    public string TagsText => Topic.Tags is { Count: > 0 } && AppSettings.Current.ListDensity != ListDensity.Compact
        ? string.Join("  ", Topic.Tags.Take(3).Select(t => $"#{t}"))
        : "";
    public bool IsPinned => Topic.IsPinned;
    public bool IsClosed => Topic.IsClosed;
    public bool ShowCategoryRow => AppSettings.Current.ListDensity != ListDensity.Compact
                                   && (!string.IsNullOrEmpty(CategoryName) || !string.IsNullOrEmpty(TagsText));
    public int TitleMaxLines => AppSettings.Current.TitleLineLimit;
    public Thickness RowPadding => new(16, AppSettings.Current.RowVerticalPadding + 4, 16, AppSettings.Current.RowVerticalPadding + 4);
    public double AvatarSize => AppSettings.Current.ListDensity == ListDensity.Compact ? 28 : 40;

    /// <summary>Optional mailbox action label (归档 / 移回收件箱).</summary>
    public string ActionLabel { get; set; } = "";
    public string MailboxAction
    {
        get => ActionLabel;
        set => ActionLabel = value;
    }
}

public partial class HomeViewModel : ObservableObject
{
    public HomeMode Mode { get; }

    [ObservableProperty] private ObservableCollection<TopicListItemViewModel> _items = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private TopPeriod _topPeriod = TopPeriod.Weekly;
    [ObservableProperty] private string _title = "最新";

    private readonly List<DiscourseTopic> _topics = [];
    private readonly Dictionary<int, DiscourseUser> _users = new();
    private int _page;
    private int _loadGeneration;

    public HomeViewModel(HomeMode mode)
    {
        Mode = mode;
        Title = mode switch
        {
            HomeMode.Latest => "最新",
            HomeMode.Top => "热门 · 本周",
            HomeMode.Newest => "新主题",
            _ => "最新"
        };
    }

    public IEnumerable<TopicListItemViewModel> VisibleItems
    {
        get
        {
            var keywords = AppSettings.Current.MutedKeywords;
            if (keywords.Count == 0) return Items;
            return Items.Where(i => !AppSettings.Current.IsTitleMuted(i.DisplayTitle));
        }
    }

    [RelayCommand]
    public async Task LoadAsync(bool force = false)
    {
        // Allow force refresh even if a previous load is in-flight (cancels via generation).
        if (IsLoading && !force) return;
        if (!force && _topics.Count > 0) return;

        IsLoading = true;
        ErrorMessage = null;
        _page = 0;
        var generation = ++_loadGeneration;
        try
        {
            // One soft UI-level retry: rapid refresh can race WebView warm / cookie sync.
            TopicListResponse? response = null;
            Exception? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (generation != _loadGeneration) return;
                try
                {
                    if (attempt > 0)
                        await Task.Delay(400);
                    response = await FetchPageAsync(0);
                    last = null;
                    break;
                }
                catch (Exception ex) when (attempt == 0 && IsSoftNetworkFailure(ex))
                {
                    last = ex;
                    AppLog.Warning("home", "load soft-retry: " + ex.Message);
                }
            }
            if (generation != _loadGeneration) return;
            if (response is null)
            {
                var fail = last ?? new Exception("加载失败");
                ErrorMessage = fail.Message;
                APIError.PostIfChallenge(fail);
                return;
            }
            Apply(response, append: false);
            HasMore = response.TopicList.MoreTopicsUrl is not null || response.TopicList.Topics.Count >= 30;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            ErrorMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoading || IsLoadingMore) return;
        IsLoadingMore = true;
        var next = _page + 1;
        var generation = _loadGeneration;
        try
        {
            var response = await FetchPageAsync(next);
            if (generation != _loadGeneration) return;
            Apply(response, append: true);
            _page = next;
            HasMore = response.TopicList.Topics.Count > 0;
            if (ErrorMessage is not null) ErrorMessage = null;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            ErrorMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    public async Task SetTopPeriodAsync(TopPeriod period)
    {
        if (Mode != HomeMode.Top || TopPeriod == period) return;
        TopPeriod = period;
        Title = $"热门 · {period.Title()}";
        _topics.Clear();
        await LoadAsync(force: true);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // Manual refresh: drop list cache for this mode so user gets fresh data,
        // but keep other caches (categories/topics) to avoid a request storm.
        ApiResponseCache.InvalidatePrefix(
            DiscourseAPI.Shared.CurrentBaseUrl.AbsoluteUri.TrimEnd('/') + "/" + Mode switch
            {
                HomeMode.Top => "top",
                HomeMode.Newest => "new",
                _ => "latest"
            });
        await LoadAsync(force: true);
    }

    private static bool IsSoftNetworkFailure(Exception ex)
    {
        // Do not soft-retry CF / challenge — that multiplies WebView aborts (log 11:33–11:34).
        if (ex is APIError { IsChallengeRelated: true }) return false;
        if (ex is APIError.CloudflareChallenge) return false;
        if (ex is APIError.Forbidden) return false;
        if (SiteAccessStore.Current.NeedsChallenge) return false;

        if (ex is APIError.Network) return true;
        if (ex is APIError.Http { Status: 0 }) return true;
        var m = ex.Message ?? "";
        return m.Contains("网络请求中断", StringComparison.Ordinal) ||
               m.Contains("HTTP 0", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("超时", StringComparison.Ordinal) ||
               m.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TopicListResponse> FetchPageAsync(int page) => Mode switch
    {
        HomeMode.Latest => await DiscourseAPI.Shared.FetchLatestAsync(page),
        HomeMode.Top => await DiscourseAPI.Shared.FetchTopAsync(TopPeriod.ApiValue(), page),
        HomeMode.Newest => await DiscourseAPI.Shared.FetchNewAsync(page),
        _ => await DiscourseAPI.Shared.FetchLatestAsync(page)
    };

    private void Apply(TopicListResponse response, bool append)
    {
        if (response.Users is not null)
        {
            foreach (var u in response.Users) _users[u.Id] = u;
            CategoryStore.Current.CacheUsers(response.Users);
        }

        if (append)
        {
            var existing = _topics.Select(t => t.Id).ToHashSet();
            var added = response.TopicList.Topics.Where(t => !existing.Contains(t.Id)).ToList();
            if (added.Count == 0) return;
            _topics.AddRange(added);
            // Append only — never replace Items (that resets ListView scroll to top).
            AppendItems(added);
        }
        else
        {
            _topics.Clear();
            _topics.AddRange(response.TopicList.Topics);
            RebuildItems();
        }
    }

    private void AppendItems(IEnumerable<DiscourseTopic> topics)
    {
        var baseUrl = AppSettings.Current.BaseUrl;
        var muted = AppSettings.Current.MutedKeywords.Count > 0;
        foreach (var t in topics)
        {
            var author = AuthorFor(t);
            var cat = CategoryStore.Current.Category(t.CategoryId);
            var vm = new TopicListItemViewModel(t, author, cat, baseUrl);
            if (muted && AppSettings.Current.IsTitleMuted(vm.DisplayTitle)) continue;
            Items.Add(vm);
        }
    }

    private void RebuildItems()
    {
        var baseUrl = AppSettings.Current.BaseUrl;
        var vms = _topics.Select(t =>
        {
            var author = AuthorFor(t);
            var cat = CategoryStore.Current.Category(t.CategoryId);
            return new TopicListItemViewModel(t, author, cat, baseUrl);
        }).ToList();

        // Filter muted
        if (AppSettings.Current.MutedKeywords.Count > 0)
            vms = vms.Where(i => !AppSettings.Current.IsTitleMuted(i.DisplayTitle)).ToList();

        // Replace collection only on full reload (not load-more).
        Items = new ObservableCollection<TopicListItemViewModel>(vms);
    }

    private DiscourseUser? AuthorFor(DiscourseTopic topic)
    {
        var poster = topic.Posters?.FirstOrDefault(p => p.Description?.Contains("Original Poster") == true)
                     ?? topic.Posters?.FirstOrDefault();
        if (poster?.UserId is int uid && _users.TryGetValue(uid, out var user))
            return user;
        return null;
    }
}
