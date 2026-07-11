using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Bookmarks;

public sealed class BookmarkItemVm
{
    public DiscourseBookmark Bookmark { get; init; } = new();
    public string Title => Bookmark.ListTitle;
    public string Subtitle => $"{Bookmark.DisplayTitle} · {RelativeDate.Describe(Bookmark.CreatedDate)}";
    public string ReminderText
    {
        get
        {
            var d = DiscourseDateParser.Parse(Bookmark.ReminderAt);
            return d is null ? "" : "⏰ 提醒 " + RelativeDate.Describe(d);
        }
    }
}

public sealed partial class BookmarksPage : Page
{
    private ListKeyboardNav? _nav;

    public BookmarksPage()
    {
        InitializeComponent();
        _nav = ListKeyboardNav.Attach(BookmarkList, item =>
        {
            if (item is BookmarkItemVm b && b.Bookmark.TopicId is int tid)
                AppRouter.Current.OpenTopic(tid, b.Bookmark.DisplayTitle, b.Bookmark.LinkedPostNumber);
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
        try
        {
            var list = await DiscourseAPI.Shared.FetchBookmarksAsync(username);
            var items = list.Select(b => new BookmarkItemVm { Bookmark = b }).ToList();
            BookmarkList.ItemsSource = items;
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

    private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItemVm item && item.Bookmark.TopicId is int tid)
            AppRouter.Current.OpenTopic(tid, item.Bookmark.DisplayTitle, item.Bookmark.LinkedPostNumber);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BookmarkItemVm item) return;
        if (!UserSessionStore.Current.RequireLogin()) return;

        var nameBox = new TextBox
        {
            Header = "名称（可选备注）",
            Text = item.Bookmark.Name ?? "",
            PlaceholderText = item.Bookmark.DisplayTitle
        };
        var reminderSwitch = new ToggleSwitch
        {
            Header = "设置提醒",
            IsOn = !string.IsNullOrEmpty(item.Bookmark.ReminderAt)
        };
        var picker = new CalendarDatePicker
        {
            Header = "提醒日期",
            Date = DiscourseDateParser.Parse(item.Bookmark.ReminderAt)?.Date
                   ?? DateTimeOffset.Now.AddDays(1).Date
        };
        var timeBox = new TimePicker
        {
            Header = "提醒时间",
            Time = DiscourseDateParser.Parse(item.Bookmark.ReminderAt)?.TimeOfDay
                   ?? new TimeSpan(9, 0, 0)
        };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = item.Bookmark.DisplayTitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                nameBox,
                reminderSwitch,
                picker,
                timeBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = "编辑书签",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            DateTimeOffset? reminder = null;
            var clear = !reminderSwitch.IsOn;
            if (reminderSwitch.IsOn && picker.Date is DateTimeOffset date)
            {
                reminder = date.Date + timeBox.Time;
                if (reminder < DateTimeOffset.Now) reminder = DateTimeOffset.Now.AddHours(1);
            }
            await DiscourseAPI.Shared.UpdateBookmarkAsync(
                item.Bookmark.Id,
                name: nameBox.Text,
                reminderAt: reminder,
                clearReminder: clear);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BookmarkItemVm item) return;
        var dialog = new ContentDialog
        {
            Title = "删除书签",
            Content = $"确认删除「{item.Title}」？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.DeleteBookmarkAsync(item.Bookmark.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
    }
}
