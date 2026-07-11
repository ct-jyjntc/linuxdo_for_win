using System.Collections.ObjectModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using LinuxDo.Features.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Messages;

public sealed partial class MessagesPage : Page
{
    private string _box = "inbox";
    private readonly ObservableCollection<TopicListItemViewModel> _items = [];

    private ListKeyboardNav? _nav;

    public MessagesPage()
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
        await LoadAsync();
    }

    private async void OnRefresh() => await LoadAsync();

    private async Task LoadAsync()
    {
        var username = UserSessionStore.Current.Username;
        if (string.IsNullOrEmpty(username)) return;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            TopicListResponse response = _box switch
            {
                "sent" => await DiscourseAPI.Shared.FetchPrivateMessagesSentAsync(username),
                "archive" => await DiscourseAPI.Shared.FetchPrivateMessagesArchiveAsync(username),
                _ => await DiscourseAPI.Shared.FetchPrivateMessagesAsync(username)
            };
            CategoryStore.Current.CacheUsers(response.Users);
            var baseUrl = AppSettings.Current.BaseUrl;
            _items.Clear();
            foreach (var t in response.TopicList.Topics)
            {
                var vm = new TopicListItemViewModel(t, null, null, baseUrl)
                {
                    MailboxAction = _box == "archive" ? "移回收件箱" : "归档"
                };
                _items.Add(vm);
            }
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
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void MailboxBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag)
        {
            _box = tag;
            await LoadAsync();
        }
    }

    private void TopicList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopicListItemViewModel item)
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
    }

    private void Compose_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        AppRouter.Current.PresentCompose(ComposeContext.PrivateMessage());
    }

    private async void ArchiveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TopicListItemViewModel item) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (_box == "archive")
                await DiscourseAPI.Shared.MovePrivateMessageToInboxAsync(item.Id);
            else
                await DiscourseAPI.Shared.ArchivePrivateMessageAsync(item.Id);
            _items.Remove(item);
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
    }
}
