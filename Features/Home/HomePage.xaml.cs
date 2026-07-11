using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace LinuxDo.Features.Home;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; private set; } = new(HomeMode.Latest);

    public HomePage()
    {
        InitializeComponent();
        AppEvents.Refresh += OnRefresh;
        AppEvents.NavigateNext += OnNavNext;
        AppEvents.NavigatePrev += OnNavPrev;
        AppEvents.QuickAction += OnQuickOpen;
        Unloaded += (_, _) =>
        {
            AppEvents.Refresh -= OnRefresh;
            AppEvents.NavigateNext -= OnNavNext;
            AppEvents.NavigatePrev -= OnNavPrev;
            AppEvents.QuickAction -= OnQuickOpen;
        };
    }

    private void OnNavNext()
    {
        if (ViewModel.Items.Count == 0) return;
        var idx = TopicList.SelectedIndex;
        if (idx < 0) idx = -1;
        var next = Math.Min(idx + 1, ViewModel.Items.Count - 1);
        TopicList.SelectedIndex = next;
        TopicList.ScrollIntoView(ViewModel.Items[next]);
    }

    private void OnNavPrev()
    {
        if (ViewModel.Items.Count == 0) return;
        var idx = TopicList.SelectedIndex;
        if (idx < 0) idx = 0;
        var prev = Math.Max(idx - 1, 0);
        TopicList.SelectedIndex = prev;
        TopicList.ScrollIntoView(ViewModel.Items[prev]);
    }

    private void OnQuickOpen()
    {
        if (TopicList.SelectedItem is TopicListItemViewModel item)
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // WinUI may box enums; don't rely only on `as HomeMode?`.
        var mode = e.Parameter is HomeMode m ? m : HomeMode.Latest;
        if (ViewModel.Mode != mode || ViewModel.Items.Count == 0)
        {
            ViewModel = new HomeViewModel(mode);
            Bindings.Update();
        }
        TopPeriodPanel.Visibility = mode == HomeMode.Top ? Visibility.Visible : Visibility.Collapsed;

        // Don't block topic list on categories — load in parallel / background.
        if (CategoryStore.Current.Categories.Count == 0)
            _ = CategoryStore.Current.LoadAsync();

        await ViewModel.LoadAsync(force: ViewModel.Items.Count == 0);
        UpdateErrorState();
    }

    private async void OnRefresh()
    {
        try
        {
            await ViewModel.RefreshAsync();
            UpdateErrorState();
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
            UpdateErrorState();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        UpdateErrorState();
    }

    private async void TopPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && Enum.TryParse<TopPeriod>(tag, out var period))
        {
            await ViewModel.SetTopPeriodAsync(period);
            UpdateErrorState();
        }
    }

    private void TopicList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopicListItemViewModel item)
        {
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
        }
    }

    private async void TopicList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Only when the last item is materializing; append-only LoadMore keeps scroll position.
        if (args.InRecycleQueue) return;
        if (args.Phase != 0) return;
        if (args.Item is TopicListItemViewModel item &&
            ViewModel.Items.Count > 0 &&
            item.Id == ViewModel.Items[^1].Id &&
            !ViewModel.IsLoadingMore &&
            !ViewModel.IsLoading &&
            ViewModel.HasMore)
        {
            await ViewModel.LoadMoreAsync();
        }
    }

    private void UpdateErrorState()
    {
        if (!string.IsNullOrEmpty(ViewModel.ErrorMessage) && ViewModel.Items.Count == 0)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = ViewModel.ErrorMessage;
            TopicList.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            TopicList.Visibility = Visibility.Visible;
        }
    }

    private void TopicList_RightTapped(object sender, RightTappedRoutedEventArgs e) { }

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TopicListItemViewModel item)
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
    }

    private void ContextOpenNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TopicListItemViewModel item)
            TopicWindowService.OpenTopic(item.Id, item.DisplayTitle);
    }

    private void ContextReadLater_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TopicListItemViewModel item)
        {
            ReadLaterStore.Current.Toggle(
                item.Id, item.DisplayTitle, item.Topic.Slug, item.Topic.CategoryId,
                item.Topic.Tags, item.Topic.PostsCount ?? item.Topic.ReplyCount,
                item.Topic.Views, item.Topic.LikeCount, item.AuthorName);
        }
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TopicListItemViewModel item)
        {
            var baseUrl = AppSettings.Current.BaseUrl.AbsoluteUri.TrimEnd('/');
            var slug = item.Topic.Slug ?? "topic";
            var url = $"{baseUrl}/t/{slug}/{item.Id}";
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
        }
    }

    private void ContextShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TopicListItemViewModel item) return;
        var baseUrl = AppSettings.Current.BaseUrl.AbsoluteUri.TrimEnd('/');
        var slug = item.Topic.Slug ?? "topic";
        var url = $"{baseUrl}/t/{slug}/{item.Id}";
        ShareService.ShareLink(App.WindowHandle, item.DisplayTitle, url);
    }
}
