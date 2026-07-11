using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public partial class UserSessionStore : ObservableObject
{
    public static UserSessionStore Current { get; } = new();

    [ObservableProperty] private AuthSession? _session;
    [ObservableProperty] private CurrentUser? _currentUser;
    [ObservableProperty] private bool _isRestoring;
    [ObservableProperty] private string? _authError;
    [ObservableProperty] private bool _isLoginPresented;
    [ObservableProperty] private bool _isAuthorizing;

    private CancellationTokenSource? _pollCts;

    public bool IsLoggedIn => CurrentUser is not null || Session is not null;
    public string? Username => CurrentUser?.Username ?? Session?.Username;
    public int UnreadCount => CurrentUser?.TotalUnread ?? 0;

    public void PresentLogin() => IsLoginPresented = true;

    public async Task RestoreAsync()
    {
        IsRestoring = true;
        try
        {
            var baseUrl = DiscourseAPI.Shared.CurrentBaseUrl;
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);

            var saved = AuthService.Shared.LoadSession();
            if (saved is not null)
            {
                Session = saved;
                if (saved.Method == AuthMethod.UserApiKey)
                    DiscourseAPI.Shared.SetCredentials(saved.ApiKey, saved.ClientId);
                else
                {
                    DiscourseAPI.Shared.SetCredentials(null, null);
                    await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
                }
                await RefreshCurrentUserAsync();
                StartBadgePolling();
            }
            else if (CookieSessionBridge.HasSessionCookie(baseUrl))
            {
                await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
                try
                {
                    var user = await DiscourseAPI.Shared.FetchCurrentUserAsync();
                    if (user is not null)
                    {
                        Session = AuthService.Shared.SaveCookieSession(user.Username, user.Id, user.AvatarTemplate);
                        CurrentUser = user;
                        StartBadgePolling();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Auth($"Bootstrap from cookies failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Auth($"Restore session failed: {ex.Message}");
            AuthError = ex.Message;
        }
        finally
        {
            IsRestoring = false;
        }
    }

    public async Task CompleteWebLoginAsync()
    {
        IsAuthorizing = true;
        AuthError = null;
        try
        {
            await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
            var user = await DiscourseAPI.Shared.FetchCurrentUserAsync();
            if (user is null)
            {
                AuthError = "未能读取登录状态，请确认已在网页中完成登录";
                return;
            }
            Session = AuthService.Shared.SaveCookieSession(user.Username, user.Id, user.AvatarTemplate);
            CurrentUser = user;
            DiscourseAPI.Shared.SetCredentials(null, null);
            IsLoginPresented = false;
            StartBadgePolling();
            AppLog.Auth($"Web login completed for {user.Username}");
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
            AppLog.Auth($"Web login failed: {ex.Message}");
        }
        finally
        {
            IsAuthorizing = false;
        }
    }

    public async Task LoginWithManualKeyAsync(string apiKey, string username)
    {
        IsAuthorizing = true;
        AuthError = null;
        try
        {
            var session = AuthService.Shared.SaveManualKey(apiKey, username);
            Session = session;
            DiscourseAPI.Shared.SetCredentials(session.ApiKey, session.ClientId);
            await RefreshCurrentUserAsync();
            if (CurrentUser is null)
            {
                AuthError = "API Key 无效或无法读取用户信息";
                return;
            }
            IsLoginPresented = false;
            StartBadgePolling();
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
        }
        finally
        {
            IsAuthorizing = false;
        }
    }

    /// <summary>Start Discourse User API Key browser authorization (may be disabled by site).</summary>
    public async Task BeginUserApiAuthAsync()
    {
        IsAuthorizing = true;
        AuthError = null;
        try
        {
            var url = AuthService.Shared.BeginAuthorization(DiscourseAPI.Shared.CurrentBaseUrl);
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
            AppLog.Auth($"Begin User API auth failed: {ex.Message}");
        }
        finally
        {
            IsAuthorizing = false;
        }
    }

    /// <summary>Complete User API Key flow from linuxdo://auth?payload=… callback.</summary>
    public async Task CompleteUserApiAuthAsync(Uri callbackUrl)
    {
        IsAuthorizing = true;
        AuthError = null;
        try
        {
            var session = await AuthService.Shared.CompleteAuthorizationAsync(callbackUrl);
            Session = session;
            DiscourseAPI.Shared.SetCredentials(session.ApiKey, session.ClientId);
            await RefreshCurrentUserAsync();
            if (CurrentUser is null)
            {
                // Still accept session if key decrypt succeeded
                CurrentUser = new CurrentUser
                {
                    Id = session.UserId ?? 0,
                    Username = session.Username,
                    AvatarTemplate = session.AvatarTemplate
                };
            }
            IsLoginPresented = false;
            StartBadgePolling();
            AppLog.Auth($"User API login completed for {session.Username}");
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
            AppLog.Auth($"User API complete failed: {ex.Message}");
        }
        finally
        {
            IsAuthorizing = false;
        }
    }

    public async Task LogoutAsync()
    {
        StopBadgePolling();
        try
        {
            AuthService.Shared.ClearSession();
            await CookieSessionBridge.ClearCookiesAsync(DiscourseAPI.Shared.CurrentBaseUrl);
        }
        catch (Exception ex)
        {
            AppLog.Auth($"Logout cleanup: {ex.Message}");
        }
        Session = null;
        CurrentUser = null;
        DiscourseAPI.Shared.ClearCredentials();
        DiscourseAPI.Shared.ResetHttpClient();
    }

    public async Task RefreshCurrentUserAsync()
    {
        try
        {
            var user = await DiscourseAPI.Shared.FetchCurrentUserAsync();
            if (user is not null)
            {
                CurrentUser = user;
                if (Session is not null)
                {
                    Session.Username = user.Username;
                    Session.UserId = user.Id;
                    Session.AvatarTemplate = user.AvatarTemplate;
                    AuthService.Shared.SaveSession(Session);
                }
                OnPropertyChanged(nameof(UnreadCount));
                OnPropertyChanged(nameof(IsLoggedIn));
                OnPropertyChanged(nameof(Username));
            }
        }
        catch (Exception ex)
        {
            AppLog.Auth($"Refresh current user: {ex.Message}");
            APIError.PostIfChallenge(ex);
        }
    }

    public bool RequireLogin()
    {
        if (IsLoggedIn) return true;
        PresentLogin();
        return false;
    }

    public void StartBadgePolling(double intervalSeconds = 120)
    {
        StopBadgePolling();
        if (!IsLoggedIn) return;
        StartMessageBus();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token); }
                catch { break; }
                if (token.IsCancellationRequested) break;
                await RefreshCurrentUserAsync();
            }
        }, token);
    }

    public void StopBadgePolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        StopMessageBus();
    }

    private void StartMessageBus()
    {
        var userId = CurrentUser?.Id ?? Session?.UserId;
        if (userId is null) return;
        var baseUrl = DiscourseAPI.Shared.CurrentBaseUrl;
        var channel = $"/notification/{userId}";
        var lastId = CurrentUser?.NotificationChannelPosition ?? -1;
        MessageBusService.Shared.Configure(baseUrl);
        MessageBusService.Shared.SetHandler(messages =>
        {
            App.DispatcherQueue?.TryEnqueue(() => HandleMessageBus(messages));
        });
        MessageBusService.Shared.Subscribe(channel, lastId);
        AppLog.Network($"MessageBus subscribed {channel}");
    }

    private void StopMessageBus()
    {
        MessageBusService.Shared.SetHandler(null);
        MessageBusService.Shared.UnsubscribeAll();
    }

    private void HandleMessageBus(IReadOnlyList<MessageBusService.Message> messages)
    {
        var didUpdate = false;
        var shouldReconcile = false;
        foreach (var message in messages)
        {
            if (message.Channel.StartsWith("/topic/", StringComparison.Ordinal)) continue;
            if (!message.Channel.StartsWith("/notification/", StringComparison.Ordinal)) continue;
            if (CurrentUser is null) continue;

            var user = CurrentUser;
            if (ToInt(message.Data.GetValueOrDefault("unread_notifications")) is int unread)
            {
                user.UnreadNotifications = unread;
                didUpdate = true;
            }
            if (ToInt(message.Data.GetValueOrDefault("unread_high_priority_notifications")) is int high)
            {
                user.UnreadHighPriorityNotifications = high;
                didUpdate = true;
            }
            if (ToInt(message.Data.GetValueOrDefault("all_unread_notifications_count")) is int all)
            {
                user.AllUnreadNotifications = all;
                didUpdate = true;
            }

            var isNew = message.Data.ContainsKey("notification_type")
                        || message.Data.ContainsKey("fancy_title")
                        || message.Data.ContainsKey("topic_id");
            if (isNew)
            {
                user.UnreadNotifications = (user.UnreadNotifications ?? 0) + 1;
                if (user.AllUnreadNotifications is int a)
                    user.AllUnreadNotifications = a + 1;
                didUpdate = true;
                shouldReconcile = true;
                if (AppSettings.Current.SystemNotificationBanners)
                    SystemToast.ShowFromNotification(message.Data);
            }

            if (didUpdate)
            {
                CurrentUser = user;
                OnPropertyChanged(nameof(UnreadCount));
            }
        }

        if (didUpdate) AppEvents.RaiseRefresh();
        if (shouldReconcile)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await RefreshCurrentUserAsync();
            });
        }
    }

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => null
    };

    partial void OnCurrentUserChanged(CurrentUser? value)
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(UnreadCount));
    }

    partial void OnSessionChanged(AuthSession? value)
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(Username));
    }
}
