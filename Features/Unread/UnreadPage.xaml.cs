using System.Collections.ObjectModel;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using LinuxDo.Features.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Unread;

public sealed partial class UnreadPage : Page
{
    private int _page;
    private bool _hasMore = true;
    private bool _loading;
    private readonly ObservableCollection<TopicListItemViewModel> _items = [];

    private ListKeyboardNav? _nav;

    public UnreadPage()
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
        if (!UserSessionStore.Current.RequireLogin()) return;
        await LoadAsync(reset: true);
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
            var response = await DiscourseAPI.Shared.FetchUnreadAsync(_page);
            CategoryStore.Current.CacheUsers(response.Users);
            var baseUrl = AppSettings.Current.BaseUrl;
            foreach (var t in response.TopicList.Topics)
            {
                if (_items.Any(i => i.Id == t.Id)) continue;
                _items.Add(new TopicListItemViewModel(t, null, CategoryStore.Current.Category(t.CategoryId), baseUrl));
            }
            _hasMore = response.TopicList.Topics.Count > 0;
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
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
