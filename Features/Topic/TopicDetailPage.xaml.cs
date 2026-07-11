using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace LinuxDo.Features.Topic;

public sealed partial class TopicDetailPage : Page
{
    public TopicDetailViewModel ViewModel { get; private set; } = new(0);

    private CancellationTokenSource? _hoverCts;
    private Flyout? _hoverFlyout;
    private string? _hoverUsername;

    public TopicDetailPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _hoverCts?.Cancel();
            try { _hoverFlyout?.Hide(); } catch { /* ignore */ }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.StopTopicBus();
        if (e.Parameter is AppRoute route && route.Kind == AppRouteKind.Topic && route.Id is int id)
        {
            ViewModel = new TopicDetailViewModel(id, route.Title, route.PostNumber);
            Bindings.Update();
            ViewModel.StartTopicBus();
            ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(TopicDetailViewModel.PendingNewPostCount)
                    or nameof(TopicDetailViewModel.ActionMessage))
                {
                    NewRepliesBar.IsOpen = ViewModel.PendingNewPostCount > 0;
                }
            };
            await ViewModel.LoadAsync();
            UpdateErrorState();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.StopTopicBus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync(force: true);
        UpdateErrorState();
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PreviousPageAsync();
        ScrollPostsToTop();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.NextPageAsync();
        ScrollPostsToTop();
    }

    private void ScrollPostsToTop()
    {
        try
        {
            if (ViewModel.Posts.Count > 0)
                PostList.ScrollIntoView(ViewModel.Posts[0], ScrollIntoViewAlignment.Leading);
            else
            {
                var sv = ListScrollPreserver.FindScrollViewer(PostList);
                sv?.ChangeView(null, 0, null, disableAnimation: true);
            }
        }
        catch { /* ignore */ }
    }
    private async void Jump_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.JumpFloorText = JumpBox.Text ?? "";
        await ViewModel.JumpToFloorAsync();
    }

    private async void LoadNewReplies_Click(object sender, RoutedEventArgs e)
        => await ViewModel.LoadNewRepliesAsync();

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            await ViewModel.ToggleLikeAsync(item);
    }

    private async void BookmarkPost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            await ViewModel.ToggleBookmarkPostAsync(item);
    }

    private async void BookmarkTopic_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ToggleTopicBookmarkAsync();

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        AppRouter.Current.PresentCompose(ComposeContext.Reply(ViewModel.TopicId, ViewModel.Title));
    }

    private void ReplyPost_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
        {
            var first = item.Blocks.FirstOrDefault(b => !string.IsNullOrEmpty(b.Text) && !b.IsImage)?.Text
                        ?? "";
            var quote = $"> @{item.AuthorName}\n> {first.Split('\n').FirstOrDefault()}\n\n";
            AppRouter.Current.PresentCompose(
                ComposeContext.Reply(ViewModel.TopicId, ViewModel.Title, item.Floor, quote));
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
        {
            AppRouter.Current.PresentCompose(
                ComposeContext.Edit(item.Id, item.Post.Raw ?? "", ViewModel.TopicId, ViewModel.Title));
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
        {
            var dialog = new ContentDialog
            {
                Title = "删除帖子",
                Content = $"确认删除 #{item.Floor} @{item.AuthorName} 的帖子？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.DeletePostAsync(item);
        }
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            await ViewModel.RecoverPostAsync(item);
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            await ViewModel.AcceptAnswerAsync(item);
    }

    private async void Flag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PostItemViewModel item) return;
        if (!UserSessionStore.Current.RequireLogin()) return;

        var box = new ComboBox
        {
            ItemsSource = Enum.GetValues<PostFlagType>().Select(t => t.Title()).ToList(),
            SelectedIndex = 0,
            Width = 280
        };
        var msg = new TextBox { Header = "补充说明（可选）", AcceptsReturn = true, Height = 80 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "选择举报类型" });
        panel.Children.Add(box);
        panel.Children.Add(msg);

        var dialog = new ContentDialog
        {
            Title = "举报帖子",
            Content = panel,
            PrimaryButtonText = "提交",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var type = Enum.GetValues<PostFlagType>()[Math.Max(0, box.SelectedIndex)];
            await ViewModel.FlagPostAsync(item, type, msg.Text);
        }
    }

    private async void React_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        // Find parent post via visual tree: Tag is reaction id, DataContext is PostItemViewModel
        var reaction = fe.Tag as string;
        if (string.IsNullOrEmpty(reaction)) return;
        var post = FindPostFromElement(fe);
        if (post is null) return;
        await ViewModel.ToggleReactionAsync(post, reaction);
    }

    private async void PollOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PollOptionVm opt) return;
        // Walk up: PollOptionVm is inside PollVm.Owner
        PollVm? poll = null;
        if (fe.DataContext is PollOptionVm && fe.Parent is FrameworkElement)
        {
            // Search posts for matching option
            foreach (var p in ViewModel.Posts)
            {
                foreach (var pl in p.Polls)
                {
                    if (pl.Options.Contains(opt))
                    {
                        poll = pl;
                        break;
                    }
                }
                if (poll is not null) break;
            }
        }
        if (poll is null) return;
        await ViewModel.VotePollAsync(poll.Owner, poll.Name, opt.Id);
    }

    private async void SharedIssue_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ToggleSharedIssueAsync();

    private async void NotifyMuted_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SetNotificationLevelAsync(TopicNotificationLevel.Muted);
    private async void NotifyRegular_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SetNotificationLevelAsync(TopicNotificationLevel.Regular);
    private async void NotifyTracking_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SetNotificationLevelAsync(TopicNotificationLevel.Tracking);
    private async void NotifyWatching_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SetNotificationLevelAsync(TopicNotificationLevel.Watching);

    private async void CloseTopic_Click(object sender, RoutedEventArgs e) => await ViewModel.CloseTopicAsync();
    private async void PinTopic_Click(object sender, RoutedEventArgs e) => await ViewModel.PinTopicAsync();
    private async void ArchiveTopic_Click(object sender, RoutedEventArgs e) => await ViewModel.ArchiveTopicAsync();

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(ViewModel.ShareUrl);
        Clipboard.SetContent(dp);
        ViewModel.ActionMessage = "链接已复制";
    }

    private void SystemShare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShareService.ShareLink(App.WindowHandle, ViewModel.Title, ViewModel.ShareUrl, ViewModel.Title);
            ViewModel.ActionMessage = "已打开系统分享";
        }
        catch (Exception ex)
        {
            ViewModel.ActionMessage = ex.Message;
        }
    }

    private void OpenNewWindow_Click(object sender, RoutedEventArgs e)
    {
        TopicWindowService.OpenTopic(ViewModel.TopicId, ViewModel.Title);
    }

    private void ExportMd_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(ViewModel.MarkdownExport);
        Clipboard.SetContent(dp);
        ViewModel.ActionMessage = "Markdown 已复制到剪贴板";
    }

    private async void ShareImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var first = ViewModel.Posts.FirstOrDefault();
            var body = first is null
                ? ViewModel.Title
                : string.Join("\n", first.Blocks.Where(b => !string.IsNullOrEmpty(b.Text) && !b.IsImage).Select(b => b.Text));
            var png = await ShareImageHelper.RenderPngAsync(
                XamlRoot,
                ViewModel.Title,
                first?.AuthorName,
                body,
                ViewModel.ShareUrl,
                AppSettings.Current.BaseUrl.Host ?? "linux.do",
                AppSettings.Current.Appearance == AppAppearance.Dark);
            if (png is null)
            {
                ViewModel.ActionMessage = "生成分享图失败";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "分享长图",
                Content = "已生成分享卡片，选择操作：",
                PrimaryButtonText = "保存 PNG",
                SecondaryButtonText = "系统分享",
                CloseButtonText = "复制到剪贴板",
                XamlRoot = XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ShareImageHelper.SavePngAsync(png);
                ViewModel.ActionMessage = "分享图已保存";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ShareService.SharePng(App.WindowHandle, ViewModel.Title, png, ViewModel.ShareUrl);
                ViewModel.ActionMessage = "已打开系统分享";
            }
            else
            {
                await ShareImageHelper.CopyPngToClipboardAsync(png);
                ViewModel.ActionMessage = "分享图已复制";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ActionMessage = ex.Message;
        }
    }

    private async void NestedReplies_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NestedRepliesDialog(ViewModel.TopicId, ViewModel.Title) { XamlRoot = XamlRoot };
        dialog.OpenPost += floor =>
        {
            ViewModel.JumpFloorText = floor.ToString();
            _ = ViewModel.JumpToFloorAsync();
        };
        await dialog.ShowAsync();
    }

    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PostItemViewModel item) return;
        if (!UserSessionStore.Current.RequireLogin()) return;
        var box = new TextBox
        {
            Header = "Boost 短评（建议 50 字内）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100
        };
        var dialog = new ContentDialog
        {
            Title = "发送 Boost",
            Content = box,
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.CreateBoostAsync(item, box.Text ?? "");
    }

    private async void DeleteBoost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BoostItemVm boost)
            await ViewModel.DeleteBoostAsync(boost.Owner, boost.Id);
    }

    private async void ReactionUsers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PostItemViewModel item) return;
        var dialog = new ReactionUsersDialog(item.Id) { XamlRoot = XamlRoot };
        dialog.OpenUser += username => AppRouter.Current.OpenUser(username);
        await dialog.ShowAsync();
    }

    private async void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.ShareUrl, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    private void ReadLater_Click(object sender, RoutedEventArgs e)
    {
        var added = ReadLaterStore.Current.Toggle(
            ViewModel.TopicId, ViewModel.Title, ViewModel.Slug, ViewModel.CategoryId,
            ViewModel.Tags, ViewModel.ReplyCount, ViewModel.Views, ViewModel.LikeCount);
        ViewModel.ActionMessage = added ? "已加入稍后阅读" : "已移出稍后阅读";
    }

    private void Avatar_Click(object sender, RoutedEventArgs e)
    {
        _hoverCts?.Cancel();
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            _ = ShowUserCardAsync(item.AuthorName, fe, sticky: true);
    }

    private void Author_Click(object sender, RoutedEventArgs e)
    {
        _hoverCts?.Cancel();
        if (sender is FrameworkElement fe && fe.Tag is PostItemViewModel item)
            _ = ShowUserCardAsync(item.AuthorName, fe, sticky: true);
    }

    private void Avatar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PostItemViewModel item) return;
        var username = item.AuthorName?.Trim() ?? "";
        if (string.IsNullOrEmpty(username) ||
            username is "system" or "anonymous") return;

        _hoverCts?.Cancel();
        _hoverCts = new CancellationTokenSource();
        var token = _hoverCts.Token;
        var anchor = fe;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(380, token);
                if (token.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested)
                        _ = ShowUserCardAsync(username, anchor, sticky: false);
                });
            }
            catch (TaskCanceledException) { /* ignore */ }
        }, token);
    }

    private void Avatar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverCts?.Cancel();
        // Delay hide so cursor can enter the flyout
        var cts = new CancellationTokenSource();
        _hoverCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, cts.Token);
                if (cts.Token.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!cts.Token.IsCancellationRequested && _hoverFlyout is not null)
                    {
                        try { _hoverFlyout.Hide(); } catch { /* ignore */ }
                        _hoverFlyout = null;
                        _hoverUsername = null;
                    }
                });
            }
            catch (TaskCanceledException) { /* ignore */ }
        }, cts.Token);
    }

    private async Task ShowUserCardAsync(string username, FrameworkElement anchor, bool sticky)
    {
        username = username.Trim();
        if (string.IsNullOrEmpty(username)) return;

        // Reuse open flyout for same user
        if (_hoverFlyout is not null &&
            string.Equals(_hoverUsername, username, StringComparison.OrdinalIgnoreCase))
            return;

        try { _hoverFlyout?.Hide(); } catch { /* ignore */ }

        var card = new UserHoverCardControl();
        var flyout = new Flyout
        {
            Content = card,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right
        };
        card.OpenProfile += u =>
        {
            flyout.Hide();
            AppRouter.Current.OpenUser(u);
        };
        card.OpenMessage += u =>
        {
            flyout.Hide();
            if (UserSessionStore.Current.RequireLogin())
                AppRouter.Current.PresentCompose(ComposeContext.PrivateMessage(u));
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_hoverFlyout, flyout))
            {
                _hoverFlyout = null;
                _hoverUsername = null;
            }
        };
        _hoverFlyout = flyout;
        _hoverUsername = username;
        flyout.ShowAt(anchor);
        await card.LoadAsync(username);
    }

    private void ExpandBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ContentBlockVm block)
            block.IsCollapsed = false;
    }

    private void CollapseBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ContentBlockVm block && block.SupportsCollapse)
            block.IsCollapsed = true;
    }

    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string url || string.IsNullOrEmpty(url)) return;
        var dialog = new ImageViewerDialog(url) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private static PostItemViewModel? FindPostFromElement(FrameworkElement fe)
    {
        DependencyObject? current = fe;
        while (current is not null)
        {
            if (current is FrameworkElement el)
            {
                if (el.DataContext is PostItemViewModel post) return post;
                if (el.Tag is PostItemViewModel tagged) return tagged;
            }
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void UpdateErrorState()
    {
        if (!string.IsNullOrEmpty(ViewModel.ErrorMessage) && ViewModel.Posts.Count == 0)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = ViewModel.ErrorMessage;
            PostList.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            PostList.Visibility = Visibility.Visible;
        }
    }
}
