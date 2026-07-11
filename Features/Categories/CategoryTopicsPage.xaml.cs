using System.Collections.ObjectModel;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using LinuxDo.Features.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Categories;

public sealed partial class CategoryTopicsPage : Page
{
    private int _categoryId;
    private string _slug = "";
    private int _page;
    private bool _hasMore = true;
    private bool _loading;
    private readonly ObservableCollection<TopicListItemViewModel> _items = [];
    private ListKeyboardNav? _nav;

    public CategoryTopicsPage()
    {
        InitializeComponent();
        TopicList.ItemsSource = _items;
        _nav = ListKeyboardNav.Attach(TopicList, item =>
        {
            if (item is TopicListItemViewModel t)
                AppRouter.Current.OpenTopic(t.Id, t.DisplayTitle);
        });
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) =>
        {
            _nav?.Dispose();
            AppEvents.Refresh -= OnRefresh;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AppRoute route && route.Kind == AppRouteKind.Category)
        {
            _categoryId = route.Id ?? 0;
            _slug = route.Slug ?? _categoryId.ToString();
            TitleText.Text = route.Name ?? "分类";
            await LoadAsync(reset: true);
        }
    }

    private async void OnRefresh() => await LoadAsync(reset: true);

    private async Task LoadAsync(bool reset)
    {
        if (_loading) return;
        _loading = true;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        try
        {
            if (reset)
            {
                _page = 0;
                _items.Clear();
                _hasMore = true;
            }
            var response = await DiscourseAPI.Shared.FetchCategoryTopicsAsync(_slug, _categoryId, _page);
            CategoryStore.Current.CacheUsers(response.Users);
            var baseUrl = AppSettings.Current.BaseUrl;
            foreach (var t in response.TopicList.Topics)
            {
                if (_items.Any(i => i.Id == t.Id)) continue;
                var author = t.Posters?.FirstOrDefault()?.UserId is int uid
                    ? CategoryStore.Current.User(uid) : null;
                _items.Add(new TopicListItemViewModel(t, author, CategoryStore.Current.Category(t.CategoryId), baseUrl));
            }
            _hasMore = response.TopicList.Topics.Count > 0;
        }
        catch (Exception ex)
        {
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            _loading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void TopicList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopicListItemViewModel item)
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
    }

    private async void TopicList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is TopicListItemViewModel item &&
            _items.Count > 0 && item.Id == _items[^1].Id && _hasMore && !_loading)
        {
            _page++;
            await LoadAsync(reset: false);
        }
    }
}
