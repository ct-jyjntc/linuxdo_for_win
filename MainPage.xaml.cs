using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using LinuxDo.Features.Auth;
using LinuxDo.Features.Bookmarks;
using LinuxDo.Features.Categories;
using LinuxDo.Features.Compose;
using LinuxDo.Features.Connect;
using LinuxDo.Features.Home;
using LinuxDo.Features.Invites;
using LinuxDo.Features.Library;
using LinuxDo.Features.Messages;
using LinuxDo.Features.Notifications;
using LinuxDo.Features.Profile;
using LinuxDo.Features.Search;
using LinuxDo.Features.Settings;
using LinuxDo.Features.Tags;
using LinuxDo.Features.Topic;
using LinuxDo.Features.Unread;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LinuxDo;

public sealed partial class MainPage : Page
{
    public AppRouter Router => AppRouter.Current;

    private bool _initialized;
    private bool _challengeDialogOpen;
    private bool _authDialogOpen;
    private bool _composeDialogOpen;
    private bool _suppressNav;

    public MainPage()
    {
        InitializeComponent();
        DataContext = this;

        AppRouter.Current.PropertyChanged += Router_PropertyChanged;
        UserSessionStore.Current.PropertyChanged += UserSession_PropertyChanged;
        SiteAccessStore.Current.PropertyChanged += SiteAccess_PropertyChanged;
        AppEvents.ApiError += OnApiError;
        AppEvents.Refresh += UpdateBadges;

        Unloaded += (_, _) =>
        {
            AppRouter.Current.PropertyChanged -= Router_PropertyChanged;
            UserSessionStore.Current.PropertyChanged -= UserSession_PropertyChanged;
            SiteAccessStore.Current.PropertyChanged -= SiteAccess_PropertyChanged;
            AppEvents.ApiError -= OnApiError;
            AppEvents.Refresh -= UpdateBadges;
        };
    }

    private async void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            WebViewAPIClient.Shared.Attach(BridgeWebView);
            await WebViewAPIClient.Shared.EnsureInitializedAsync();
            CookieSessionBridge.PrimeUserAgent();
        }
        catch (Exception ex)
        {
            AppLog.Warning("webview", "Bridge init: " + ex.Message);
        }

        await DiscourseAPI.Shared.UpdateBaseUrlAsync(AppSettings.Current.BaseUrl);
        SettingsPage.ApplyTheme(AppSettings.Current.Appearance);

        // Don't block first navigation on categories (can hang under CF).
        // Warm WebView + restore session in background; navigate immediately.
        _ = WebViewAPIClient.Shared.EnsureWarmAsync(AppSettings.Current.BaseUrl, 3);
        _ = CategoryStore.Current.LoadAsync();

        try
        {
            await UserSessionStore.Current.RestoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warning("session", "Restore: " + ex.Message);
        }

        UpdateAccountFooter();
        UpdateBadges();
        NavigateToRoute(AppRouter.Current.Route, isRoot: true);

        // Clipboard topic-link watch when window is activated
        if (App.Window is not null)
        {
            App.Window.Activated += async (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated) return;
                await CheckClipboardTopicLinkAsync();
            };
        }
    }

    private string _lastClipboard = "";

    private async Task CheckClipboardTopicLinkAsync()
    {
        if (!AppSettings.Current.WatchClipboardForTopicLinks) return;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            var text = (await content.GetTextAsync())?.Trim();
            if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
            _lastClipboard = text;
            var route = DeepLinkRouter.RouteFrom(text);
            if (route is null || route.Kind != AppRouteKind.Topic || route.Id is null) return;
            if (AppRouter.Current.Route.Kind == AppRouteKind.Topic &&
                AppRouter.Current.Route.Id == route.Id) return;

            var dialog = new ContentDialog
            {
                Title = "打开主题链接？",
                Content = text.Length > 200 ? text[..200] + "…" : text,
                PrimaryButtonText = "打开",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                AppRouter.Current.OpenTopic(route.Id.Value, route.Title, route.PostNumber);
        }
        catch
        {
            // clipboard access can fail if another app owns it
        }
    }

    private void Router_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppRouter.Route) or nameof(AppRouter.CanGoBack))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.PropertyName == nameof(AppRouter.Route))
                    NavigateToRoute(AppRouter.Current.Route, isRoot: AppRouter.Current.Route.IsRoot);
                Bindings.Update();
            });
        }

        if (e.PropertyName == nameof(AppRouter.IsComposePresented) && AppRouter.Current.IsComposePresented)
        {
            DispatcherQueue.TryEnqueue(async () => await ShowComposeAsync());
        }
    }

    private void UserSession_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateAccountFooter();
            UpdateBadges();
            if (e.PropertyName == nameof(UserSessionStore.IsLoginPresented) &&
                UserSessionStore.Current.IsLoginPresented)
            {
                _ = ShowAuthAsync();
            }
        });
    }

    private void SiteAccess_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SiteAccessStore.NeedsChallenge) &&
            SiteAccessStore.Current.NeedsChallenge)
        {
            DispatcherQueue.TryEnqueue(async () => await ShowChallengeAsync());
        }
    }

    private void OnApiError(Exception ex)
    {
        DispatcherQueue.TryEnqueue(() => SiteAccessStore.Current.HandleApiError(ex));
    }

    private void NavigateToRoute(AppRoute route, bool isRoot)
    {
        _suppressNav = true;
        try
        {
            // Sync sidebar selection for root routes
            var tag = route.Kind switch
            {
                AppRouteKind.Latest => "latest",
                AppRouteKind.Top => "top",
                AppRouteKind.New => "new",
                AppRouteKind.Categories or AppRouteKind.Category => "categories",
                AppRouteKind.Tags or AppRouteKind.Tag => "tags",
                AppRouteKind.Search => "search",
                AppRouteKind.Unread => "unread",
                AppRouteKind.Notifications => "notifications",
                AppRouteKind.Messages => "messages",
                AppRouteKind.Bookmarks => "bookmarks",
                AppRouteKind.Drafts => "drafts",
                AppRouteKind.ReadLater => "readlater",
                AppRouteKind.History => "history",
                AppRouteKind.Profile or AppRouteKind.User => "profile",
                AppRouteKind.Invites => "invites",
                AppRouteKind.TrustLevel => "trust",
                AppRouteKind.Settings => "settings",
                _ => null
            };

            if (tag is not null)
            {
                foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
                {
                    if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                    {
                        NavView.SelectedItem = item;
                        break;
                    }
                }
            }
            else
            {
                NavView.SelectedItem = null;
            }

            switch (route.Kind)
            {
                case AppRouteKind.Latest:
                    ContentFrame.Navigate(typeof(HomePage), HomeMode.Latest);
                    break;
                case AppRouteKind.Top:
                    ContentFrame.Navigate(typeof(HomePage), HomeMode.Top);
                    break;
                case AppRouteKind.New:
                    ContentFrame.Navigate(typeof(HomePage), HomeMode.Newest);
                    break;
                case AppRouteKind.Unread:
                    ContentFrame.Navigate(typeof(UnreadPage));
                    break;
                case AppRouteKind.Categories:
                    ContentFrame.Navigate(typeof(CategoriesPage));
                    break;
                case AppRouteKind.Category:
                    ContentFrame.Navigate(typeof(CategoryTopicsPage), route);
                    break;
                case AppRouteKind.Tags:
                    ContentFrame.Navigate(typeof(TagsPage));
                    break;
                case AppRouteKind.Tag:
                    ContentFrame.Navigate(typeof(TagTopicsPage), route);
                    break;
                case AppRouteKind.Search:
                    ContentFrame.Navigate(typeof(SearchPage));
                    break;
                case AppRouteKind.Notifications:
                    ContentFrame.Navigate(typeof(NotificationsPage));
                    break;
                case AppRouteKind.Messages:
                    ContentFrame.Navigate(typeof(MessagesPage));
                    break;
                case AppRouteKind.Bookmarks:
                    ContentFrame.Navigate(typeof(BookmarksPage));
                    break;
                case AppRouteKind.Drafts:
                    ContentFrame.Navigate(typeof(DraftsPage));
                    break;
                case AppRouteKind.ReadLater:
                    ContentFrame.Navigate(typeof(LocalListsPage), "readlater");
                    break;
                case AppRouteKind.History:
                    ContentFrame.Navigate(typeof(LocalListsPage), "history");
                    break;
                case AppRouteKind.Profile:
                    ContentFrame.Navigate(typeof(ProfilePage));
                    break;
                case AppRouteKind.User:
                    ContentFrame.Navigate(typeof(ProfilePage), route);
                    break;
                case AppRouteKind.Invites:
                    ContentFrame.Navigate(typeof(InvitesPage));
                    break;
                case AppRouteKind.TrustLevel:
                    ContentFrame.Navigate(typeof(TrustLevelPage));
                    break;
                case AppRouteKind.Settings:
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
                case AppRouteKind.Topic:
                    ContentFrame.Navigate(typeof(TopicDetailPage), route);
                    break;
            }
        }
        finally
        {
            _suppressNav = false;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_suppressNav) return;
        if (args.InvokedItemContainer is not NavigationViewItem item) return;
        var tag = item.Tag?.ToString();
        if (string.IsNullOrEmpty(tag)) return;

        var needsLogin = tag is "unread" or "notifications" or "messages" or "bookmarks"
            or "drafts" or "profile" or "invites" or "trust";
        if (needsLogin && !UserSessionStore.Current.IsLoggedIn)
        {
            UserSessionStore.Current.PresentLogin();
            return;
        }

        AppRoute route = tag switch
        {
            "latest" => AppRoute.Latest,
            "top" => AppRoute.Top,
            "new" => AppRoute.New,
            "categories" => AppRoute.Categories,
            "tags" => AppRoute.Tags,
            "search" => AppRoute.Search,
            "unread" => AppRoute.Unread,
            "notifications" => AppRoute.Notifications,
            "messages" => AppRoute.Messages,
            "bookmarks" => AppRoute.Bookmarks,
            "drafts" => AppRoute.Drafts,
            "readlater" => AppRoute.ReadLater,
            "history" => AppRoute.History,
            "profile" => AppRoute.Profile,
            "invites" => AppRoute.Invites,
            "trust" => AppRoute.TrustLevel,
            "settings" => AppRoute.Settings,
            _ => AppRoute.Latest
        };
        AppRouter.Current.SelectRoot(route);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        => AppRouter.Current.GoBack();

    private void Back_Click(object sender, RoutedEventArgs e)
        => AppRouter.Current.GoBack();

    private void Search_Click(object sender, RoutedEventArgs e)
        => AppRouter.Current.SelectRoot(AppRoute.Search);

    private void Compose_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        AppRouter.Current.PresentCompose(ComposeContext.NewTopic());
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Skip when focus is in a text input
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
            return;

        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                   & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        var action = ShortcutStore.Current.Match(e.Key, ctrl, shift, alt);
        if (action is null) return;

        switch (action.Value)
        {
            case AppShortcutAction.NewMessage:
                if (UserSessionStore.Current.RequireLogin())
                    AppRouter.Current.PresentCompose(ComposeContext.PrivateMessage());
                e.Handled = true;
                break;
            case AppShortcutAction.NewTopic:
                if (UserSessionStore.Current.RequireLogin())
                    AppRouter.Current.PresentCompose(ComposeContext.NewTopic());
                e.Handled = true;
                break;
            case AppShortcutAction.Search:
                AppRouter.Current.SelectRoot(AppRoute.Search);
                e.Handled = true;
                break;
            case AppShortcutAction.Refresh:
                AppEvents.RaiseRefresh();
                e.Handled = true;
                break;
            case AppShortcutAction.GoLatest:
                AppRouter.Current.SelectRoot(AppRoute.Latest);
                e.Handled = true;
                break;
            case AppShortcutAction.GoTop:
                AppRouter.Current.SelectRoot(AppRoute.Top);
                e.Handled = true;
                break;
            case AppShortcutAction.GoUnread:
                if (UserSessionStore.Current.RequireLogin())
                    AppRouter.Current.SelectRoot(AppRoute.Unread);
                e.Handled = true;
                break;
            case AppShortcutAction.GoNotifications:
                if (UserSessionStore.Current.RequireLogin())
                    AppRouter.Current.SelectRoot(AppRoute.Notifications);
                e.Handled = true;
                break;
            case AppShortcutAction.GoMessages:
                if (UserSessionStore.Current.RequireLogin())
                    AppRouter.Current.SelectRoot(AppRoute.Messages);
                e.Handled = true;
                break;
            case AppShortcutAction.GoReadLater:
                AppRouter.Current.SelectRoot(AppRoute.ReadLater);
                e.Handled = true;
                break;
            case AppShortcutAction.GoHistory:
                AppRouter.Current.SelectRoot(AppRoute.History);
                e.Handled = true;
                break;
            case AppShortcutAction.ListNext:
                AppEvents.RaiseNavigateNext();
                e.Handled = true;
                break;
            case AppShortcutAction.ListPrev:
                AppEvents.RaiseNavigatePrev();
                e.Handled = true;
                break;
            case AppShortcutAction.ListOpen:
                AppEvents.RaiseQuickAction();
                e.Handled = true;
                break;
        }
    }

    private void LoginOrNotif_Click(object sender, RoutedEventArgs e)
    {
        if (UserSessionStore.Current.IsLoggedIn)
            AppRouter.Current.SelectRoot(AppRoute.Notifications);
        else
            UserSessionStore.Current.PresentLogin();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => AppEvents.RaiseRefresh();

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserSessionStore.Current.IsLoggedIn)
            AppRouter.Current.SelectRoot(AppRoute.Profile);
        else
            UserSessionStore.Current.PresentLogin();
    }

    private int _accountAvatarGen;

    private void UpdateAccountFooter()
    {
        var session = UserSessionStore.Current;
        if (session.IsLoggedIn)
        {
            AccountNameText.Text = session.Username ?? "已登录";
            AccountHintText.Text = "查看个人资料";
            LoginOrNotifButton.Label = session.UnreadCount > 0 ? $"通知 ({session.UnreadCount})" : "通知";
            LoginOrNotifButton.Icon = new SymbolIcon(Symbol.OutlineStar);

            var avatarUrl = session.CurrentUser?.AvatarUrl(AppSettings.Current.BaseUrl, 64)
                            ?? session.Session?.AvatarUrl(AppSettings.Current.BaseUrl, 64);
            if (avatarUrl is not null)
            {
                AccountPlaceholderIcon.Visibility = Visibility.Collapsed;
                AccountAvatarImage.Visibility = Visibility.Visible;
                var uri = avatarUrl.AbsoluteUri;
                AccountAvatarImage.SourceUrl = uri;
                // Warm cache + force apply (footer starts collapsed; template may miss first bind)
                _ = EnsureAccountAvatarAsync(uri);
            }
            else
            {
                AccountAvatarImage.SourceUrl = null;
                AccountAvatarImage.Visibility = Visibility.Collapsed;
                AccountPlaceholderIcon.Visibility = Visibility.Visible;
            }
        }
        else
        {
            AccountNameText.Text = "未登录";
            AccountHintText.Text = "点击登录";
            LoginOrNotifButton.Label = "登录";
            LoginOrNotifButton.Icon = new SymbolIcon(Symbol.Contact);
            AccountAvatarImage.SourceUrl = null;
            AccountAvatarImage.Visibility = Visibility.Collapsed;
            AccountPlaceholderIcon.Visibility = Visibility.Visible;
        }
    }

    private async Task EnsureAccountAvatarAsync(string url)
    {
        var gen = ++_accountAvatarGen;
        try
        {
            // Prefetch into ImageLoader memory (UI-thread BitmapImage)
            _ = await ImageLoader.Shared.LoadAsync(url);
            if (gen != _accountAvatarGen) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _accountAvatarGen) return;
                if (!UserSessionStore.Current.IsLoggedIn) return;
                AccountPlaceholderIcon.Visibility = Visibility.Collapsed;
                AccountAvatarImage.Visibility = Visibility.Visible;
                // Re-set to trigger CachedImage load from warm cache
                if (AccountAvatarImage.SourceUrl == url)
                {
                    AccountAvatarImage.SourceUrl = null;
                    AccountAvatarImage.SourceUrl = url;
                }
                else
                {
                    AccountAvatarImage.SourceUrl = url;
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("avatar", "account footer: " + ex.Message);
        }
    }

    private void UpdateBadges()
    {
        var unread = UserSessionStore.Current.UnreadCount;
        NavNotifications.InfoBadge = unread > 0
            ? new InfoBadge { Value = unread }
            : null;

        var drafts = DraftStore.Current.Drafts.Count;
        NavDrafts.InfoBadge = drafts > 0
            ? new InfoBadge { Value = drafts }
            : null;

        if (AppSettings.Current.ShowLocalListBadges)
        {
            var later = ReadLaterStore.Current.Count;
            NavReadLater.InfoBadge = later > 0 ? new InfoBadge { Value = later } : null;
            var hist = ReadingHistoryStore.Current.Items.Count;
            NavHistory.InfoBadge = hist > 0 ? new InfoBadge { Value = Math.Min(hist, 99) } : null;
        }
        else
        {
            NavReadLater.InfoBadge = null;
            NavHistory.InfoBadge = null;
        }
    }

    private async Task ShowAuthAsync()
    {
        if (_authDialogOpen) return;
        _authDialogOpen = true;
        try
        {
            var dialog = new AuthDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        finally
        {
            _authDialogOpen = false;
            UserSessionStore.Current.IsLoginPresented = false;
            UpdateAccountFooter();
            UpdateBadges();
        }
    }

    private async Task ShowChallengeAsync()
    {
        if (_challengeDialogOpen) return;
        _challengeDialogOpen = true;
        try
        {
            var dialog = new SiteChallengeDialog { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        finally
        {
            _challengeDialogOpen = false;
            // Always refresh after challenge dialog closes (success or force-continue).
            UpdateAccountFooter();
            AppEvents.RaiseRefresh();
        }
    }

    private async Task ShowComposeAsync()
    {
        if (_composeDialogOpen) return;
        _composeDialogOpen = true;
        try
        {
            var dialog = new ComposeDialog(AppRouter.Current.ComposeContext) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        finally
        {
            _composeDialogOpen = false;
            AppRouter.Current.IsComposePresented = false;
        }
    }
}
