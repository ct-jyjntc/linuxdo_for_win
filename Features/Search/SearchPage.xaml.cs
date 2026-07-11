using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Search;

public sealed class SearchResultItem
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public int? TopicId { get; set; }
    public int? PostNumber { get; set; }
    public string? Username { get; set; }
}

public sealed partial class SearchPage : Page
{
    private sealed record CategoryOption(int? Id, string Name, string? Slug)
    {
        public override string ToString() => Name;
    }

    private ListKeyboardNav? _nav;

    public SearchPage()
    {
        InitializeComponent();
        _nav = ListKeyboardNav.Attach(ResultList, item =>
        {
            if (item is not SearchResultItem r) return;
            if (r.TopicId is int tid)
                AppRouter.Current.OpenTopic(tid, r.Title, r.PostNumber);
            else if (!string.IsNullOrEmpty(r.Username))
                AppRouter.Current.OpenUser(r.Username!);
        });
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) =>
        {
            _nav?.Dispose();
            AppEvents.Refresh -= OnRefresh;
        };
    }

    private async void OnRefresh()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(q))
            await RunSearchAsync(q);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Don't block search UI on categories
        if (CategoryStore.Current.Categories.Count == 0)
            _ = CategoryStore.Current.LoadAsync();
        try
        {
            if (CategoryStore.Current.Categories.Count == 0)
                await CategoryStore.Current.LoadAsync();
        }
        catch { /* ignore */ }
        var cats = new List<CategoryOption> { new(null, "全部分类", null) };
        cats.AddRange(CategoryStore.Current.TopLevelCategories
            .Select(c => new CategoryOption(c.Id, c.Name, c.Slug)));
        CategoryBox.ItemsSource = cats;
        if (CategoryBox.SelectedIndex < 0) CategoryBox.SelectedIndex = 0;
    }

    private void ToggleFilters_Click(object sender, RoutedEventArgs e)
    {
        FiltersPanel.Visibility = FiltersPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
        => await RunSearchAsync(SearchBox.Text ?? "");

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => await RunSearchAsync(args.QueryText ?? sender.Text ?? "");

    private string BuildQuery(string raw)
    {
        var parts = new List<string>();
        var q = raw.Trim();
        if (!string.IsNullOrEmpty(q)) parts.Add(q);
        if (TitleOnlySwitch.IsOn) parts.Add("in:title");
        if (CategoryBox.SelectedItem is CategoryOption { Id: not null } cat)
        {
            var slug = cat.Slug ?? cat.Id.ToString();
            parts.Add($"category:{slug}");
        }
        var tag = TagBox.Text?.Trim();
        if (!string.IsNullOrEmpty(tag))
            parts.Add(tag.StartsWith('#') ? tag : "#" + tag);
        var user = UserBox.Text?.Trim();
        if (!string.IsNullOrEmpty(user))
            parts.Add(user.StartsWith('@') ? user : "@" + user);
        return string.Join(' ', parts);
    }

    private async Task RunSearchAsync(string raw)
    {
        var q = BuildQuery(raw);
        if (string.IsNullOrWhiteSpace(q))
        {
            EmptyText.Text = "请输入关键词";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        ResultList.Visibility = Visibility.Collapsed;
        try
        {
            var response = await DiscourseAPI.Shared.SearchAsync(q);
            var items = new List<SearchResultItem>();
            var seenTopics = new HashSet<int>();

            if (response.Topics is not null)
            {
                foreach (var t in response.Topics)
                {
                    seenTopics.Add(t.Id);
                    items.Add(new SearchResultItem
                    {
                        Title = t.DisplayTitle,
                        Subtitle = $"主题 · 💬 {t.PostsCount ?? 0} · {RelativeDate.Describe(t.BumpedDate ?? t.CreatedDate)}",
                        TopicId = t.Id
                    });
                }
            }

            if (response.Posts is not null)
            {
                foreach (var p in response.Posts)
                {
                    // Prefer topic card if already present; still show unique posts
                    var blurb = HtmlText.PlainText(p.Blurb);
                    if (string.IsNullOrWhiteSpace(blurb)) blurb = $"(帖子 #{p.PostNumber})";
                    items.Add(new SearchResultItem
                    {
                        Title = blurb,
                        Subtitle = $"帖子 · @{p.Username} · #{p.PostNumber}",
                        TopicId = p.TopicId,
                        PostNumber = p.PostNumber
                    });
                }
            }

            if (response.Users is not null)
            {
                foreach (var u in response.Users)
                {
                    items.Add(new SearchResultItem
                    {
                        Title = u.Username,
                        Subtitle = u.Name ?? "用户",
                        Username = u.Username
                    });
                }
            }

            ResultList.ItemsSource = items;
            if (items.Count == 0)
            {
                EmptyText.Text = $"没有找到结果\n查询：{q}";
                EmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                ResultList.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            EmptyText.Text = "搜索失败：" + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item) return;
        if (item.TopicId is int tid)
            AppRouter.Current.OpenTopic(tid, item.Title, item.PostNumber);
        else if (!string.IsNullOrEmpty(item.Username))
            AppRouter.Current.OpenUser(item.Username!);
    }
}
