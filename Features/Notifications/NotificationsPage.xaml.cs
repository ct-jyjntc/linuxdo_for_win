using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Notifications;

public sealed class NotifItemVm
{
    public DiscourseNotification Notification { get; init; } = new();
    public string Title => Notification.Title;
    public string Subtitle => $"{Notification.TypeDescription} · {RelativeDate.Describe(Notification.CreatedDate)}{(Notification.Read == false ? " · 未读" : "")}";
}

public sealed partial class NotificationsPage : Page
{
    private ListKeyboardNav? _nav;

    public NotificationsPage()
    {
        InitializeComponent();
        _nav = ListKeyboardNav.Attach(NotifList, item =>
        {
            if (item is NotifItemVm n && n.Notification.TopicId is int tid)
                AppRouter.Current.OpenTopic(tid, n.Notification.Title, n.Notification.PostNumber);
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
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var list = await DiscourseAPI.Shared.FetchNotificationsAsync();
            var items = list.Select(n => new NotifItemVm { Notification = n }).ToList();
            NotifList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.MarkNotificationsReadAsync();
            await UserSessionStore.Current.RefreshCurrentUserAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            APIError.PostIfChallenge(ex);
        }
    }

    private void NotifList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotifItemVm item && item.Notification.TopicId is int tid)
            AppRouter.Current.OpenTopic(tid, item.Notification.Title, item.Notification.PostNumber);
    }
}
