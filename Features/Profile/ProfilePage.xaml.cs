using System.Collections.ObjectModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using LinuxDo.Features.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Profile;

public sealed class ActivityItemVm
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int? TopicId { get; init; }
    public int? PostNumber { get; init; }
}

public sealed partial class ProfilePage : Page
{
    private string _username = "";
    private UserProfile? _profile;
    private UserActionFilter _activityFilter = UserActionFilter.All;
    private readonly ObservableCollection<TopicListItemViewModel> _topics = [];
    private readonly ObservableCollection<ActivityItemVm> _activities = [];

    public ProfilePage()
    {
        InitializeComponent();
        TopicList.ItemsSource = _topics;
        ActivityList.ItemsSource = _activities;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AppRoute { Kind: AppRouteKind.User, Username: not null } route)
            _username = route.Username!;
        else if (e.Parameter is string s)
            _username = s;
        else
            _username = UserSessionStore.Current.Username ?? "";

        if (string.IsNullOrEmpty(_username))
        {
            if (!UserSessionStore.Current.RequireLogin()) return;
            _username = UserSessionStore.Current.Username ?? "";
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        ErrorText.Text = "";
        try
        {
            _profile = await DiscourseAPI.Shared.FetchUserProfileAsync(_username);
            DisplayNameText.Text = _profile.DisplayName;
            UsernameText.Text = "@" + _profile.Username;
            TitleText.Text = _profile.Title ?? "";
            BioText.Text = HtmlText.PlainText(_profile.BioCooked ?? _profile.BioExcerpt ?? _profile.BioRaw ?? "");
            TrustText.Text = (_profile.TrustLevel ?? 0).ToString();
            ViewsText.Text = (_profile.ProfileViewCount ?? 0).ToString();
            FollowersText.Text = (_profile.TotalFollowers ?? 0).ToString();
            FollowingText.Text = (_profile.TotalFollowing ?? 0).ToString();

            var avatar = _profile.AvatarUrl(AppSettings.Current.BaseUrl, 240);
            AvatarImage.SourceUrl = avatar?.AbsoluteUri;

            var isSelf = string.Equals(_username, UserSessionStore.Current.Username, StringComparison.OrdinalIgnoreCase);
            LogoutButton.Visibility = isSelf ? Visibility.Visible : Visibility.Collapsed;
            FollowButton.Visibility = !isSelf && _profile.CanFollow == true ? Visibility.Visible : Visibility.Collapsed;
            MessageButton.Visibility = !isSelf ? Visibility.Visible : Visibility.Collapsed;
            MuteButton.Visibility = !isSelf ? Visibility.Visible : Visibility.Collapsed;
            IgnoreButton.Visibility = !isSelf ? Visibility.Visible : Visibility.Collapsed;
            FollowersButton.Visibility = Visibility.Visible;
            FollowingButton.Visibility = Visibility.Visible;
            FollowButton.Content = _profile.IsFollowed == true ? "取消关注" : "关注";
            MuteButton.Content = _profile.Muted == true ? "取消静音" : "静音";
            IgnoreButton.Content = _profile.Ignored == true ? "取消忽略" : "忽略";

            // Summary (best effort)
            try
            {
                var summary = await DiscourseAPI.Shared.FetchUserSummaryAsync(_username);
                var s = summary.Summary;
                LikesGivenText.Text = (s?.LikesGiven ?? 0).ToString();
                LikesReceivedText.Text = (s?.LikesReceived ?? 0).ToString();
                TopicCountText.Text = (s?.TopicCount ?? 0).ToString();
                PostCountText.Text = (s?.PostCount ?? 0).ToString();
                DaysVisitedText.Text = (s?.DaysVisited ?? 0).ToString();
                PostsReadText.Text = (s?.PostsReadCount ?? 0).ToString();
                SolvedText.Text = (s?.SolvedCount ?? 0).ToString();
                BookmarkCountText.Text = (s?.BookmarkCount ?? 0).ToString();
            }
            catch
            {
                // summary optional
            }

            await LoadActivityAsync();

            var topics = await DiscourseAPI.Shared.FetchUserTopicsAsync(_username);
            _topics.Clear();
            var baseUrl = AppSettings.Current.BaseUrl;
            foreach (var t in topics.TopicList.Topics)
                _topics.Add(new TopicListItemViewModel(t, null, CategoryStore.Current.Category(t.CategoryId), baseUrl));
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async Task LoadActivityAsync()
    {
        try
        {
            var actions = await DiscourseAPI.Shared.FetchUserActionsAsync(_username, _activityFilter, limit: 30);
            _activities.Clear();
            foreach (var a in actions)
            {
                _activities.Add(new ActivityItemVm
                {
                    Title = a.DisplayTitle,
                    Subtitle = $"{RelativeDate.Describe(a.CreatedDate)}" +
                               (a.PostNumber is int pn ? $" · #{pn}" : ""),
                    TopicId = a.TopicId,
                    PostNumber = a.PostNumber
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("profile", "activity: " + ex.Message);
        }
    }

    private async void ActivityBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag) return;
        _activityFilter = tag switch
        {
            "Topics" => UserActionFilter.Topics,
            "Replies" => UserActionFilter.Replies,
            "LikesReceived" => UserActionFilter.LikesReceived,
            _ => UserActionFilter.All
        };
        await LoadActivityAsync();
    }

    private void ActivityList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ActivityItemVm item && item.TopicId is int tid)
            AppRouter.Current.OpenTopic(tid, item.Title, item.PostNumber);
    }

    private async void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin() || _profile is null) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (_profile.IsFollowed == true)
                await DiscourseAPI.Shared.UnfollowUserAsync(_username);
            else
                await DiscourseAPI.Shared.FollowUserAsync(_username);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    private void Message_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        AppRouter.Current.PresentCompose(ComposeContext.PrivateMessage(_username));
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin() || _profile is null) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            var next = _profile.Muted == true ? UserNotificationLevel.Normal : UserNotificationLevel.Mute;
            await DiscourseAPI.Shared.SetUserNotificationLevelAsync(_username, next);
            ErrorText.Text = next == UserNotificationLevel.Mute ? "已静音" : "已恢复正常";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin() || _profile is null) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (_profile.Ignored == true)
            {
                await DiscourseAPI.Shared.SetUserNotificationLevelAsync(_username, UserNotificationLevel.Normal);
                ErrorText.Text = "已取消忽略";
                await LoadAsync();
                return;
            }

            var options = new (string title, TimeSpan span)[]
            {
                ("4 小时", TimeSpan.FromHours(4)),
                ("明天", TimeSpan.FromDays(1)),
                ("一周", TimeSpan.FromDays(7)),
                ("两周", TimeSpan.FromDays(14)),
                ("一个月", TimeSpan.FromDays(30)),
                ("三个月", TimeSpan.FromDays(90)),
                ("六个月", TimeSpan.FromDays(180)),
                ("一年", TimeSpan.FromDays(365)),
                ("永久", TimeSpan.FromDays(365000))
            };
            var list = new ListView
            {
                ItemsSource = options.Select(o => o.title).ToList(),
                SelectionMode = ListViewSelectionMode.Single,
                SelectedIndex = 2,
                Height = 280
            };
            var dialog = new ContentDialog
            {
                Title = "忽略用户",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "忽略后将不再看到该用户的帖子与私信（直到到期）。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                        list
                    }
                },
                PrimaryButtonText = "确认忽略",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var idx = Math.Max(0, list.SelectedIndex);
            var exp = DateTimeOffset.UtcNow.Add(options[idx].span);
            var expiringAt = DiscourseDateParser.Format(exp);
            await DiscourseAPI.Shared.SetUserNotificationLevelAsync(_username, UserNotificationLevel.Ignore, expiringAt);
            ErrorText.Text = "已忽略";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    private async void Followers_Click(object sender, RoutedEventArgs e)
        => await ShowFollowListAsync(false);

    private async void Following_Click(object sender, RoutedEventArgs e)
        => await ShowFollowListAsync(true);

    private async Task ShowFollowListAsync(bool following)
    {
        try
        {
            var users = following
                ? await DiscourseAPI.Shared.FetchFollowingAsync(_username)
                : await DiscourseAPI.Shared.FetchFollowersAsync(_username);
            var list = new ListView
            {
                ItemsSource = users.Select(u => $"{u.DisplayName} (@{u.Username})").ToList(),
                Height = 360,
                IsItemClickEnabled = true
            };
            list.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is string s)
                {
                    var at = s.LastIndexOf("(@", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        var name = s[(at + 2)..].TrimEnd(')');
                        AppRouter.Current.OpenUser(name);
                    }
                }
            };
            var dialog = new ContentDialog
            {
                Title = following ? "关注列表" : "粉丝列表",
                Content = users.Count == 0 ? new TextBlock { Text = "暂无数据" } : list,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await UserSessionStore.Current.LogoutAsync();
        AppRouter.Current.SelectRoot(AppRoute.Latest);
    }

    private void TopicList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopicListItemViewModel item)
            AppRouter.Current.OpenTopic(item.Id, item.DisplayTitle);
    }
}
