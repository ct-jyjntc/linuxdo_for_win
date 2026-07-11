using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Dispatching;

namespace LinuxDo.Core.Services;

public partial class SiteAccessStore : ObservableObject
{
    public static SiteAccessStore Current { get; } = new();

    [ObservableProperty] private bool _needsChallenge;
    [ObservableProperty] private string _challengeMessage =
        "LinuxDo 启用了 Cloudflare 防护。已自动打开验证窗口，完成后一般会自动进入。";
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string? _lastError;

    private bool _isCleared;
    private DateTime? _suppressAutoPresentUntil;
    private int _presentCount;
    private const double SuppressIntervalSeconds = 45;
    private DispatcherQueue? _uiQueue;

    public bool IsCleared => _isCleared;

    public void BindDispatcher(DispatcherQueue queue) => _uiQueue = queue;

    public void PresentChallenge(string? message = null, bool force = false)
    {
        if (!force)
        {
            if (_suppressAutoPresentUntil is not null && DateTime.UtcNow < _suppressAutoPresentUntil)
                return;
            if (NeedsChallenge) return;
            if (_presentCount > 6) return;
        }
        else
        {
            _suppressAutoPresentUntil = null;
            _isCleared = false;
            if (NeedsChallenge) NeedsChallenge = false;
        }

        if (message is not null) ChallengeMessage = message;
        else if (string.IsNullOrWhiteSpace(ChallengeMessage))
            ChallengeMessage = "检测到站点防护拦截，请在窗口内完成验证（通常会自动继续）。";

        NeedsChallenge = false;
        NeedsChallenge = true;
        if (force) _presentCount = 0;
        else _presentCount++;
        LastError = null;
        IsVerifying = false;

        ApiResponseCache.Pause(TimeSpan.FromMinutes(3));
        MessageBusService.Shared.Pause(TimeSpan.FromMinutes(3));
        UserSessionStore.Current.StopBadgePolling();
        ImageLoader.Shared.Pause(TimeSpan.FromMinutes(3));
        AppLog.Auth($"PresentChallenge force={force} count={_presentCount}");
    }

    public void HandleApiError(Exception error)
    {
        if (error is APIError api && api.IsChallengeRelated)
        {
            PresentChallenge(api.Message);
            return;
        }
        var text = error.Message.ToLowerInvariant();
        if (text.Contains("cloudflare") || text.Contains("人机验证") || text.Contains("just a moment")
            || text.Contains("cf_clearance") || text.Contains("non json") || text.Contains("非 json"))
        {
            PresentChallenge(error.Message);
        }
    }

    public void DismissWithoutClearing()
    {
        NeedsChallenge = false;
        IsVerifying = false;
        _suppressAutoPresentUntil = DateTime.UtcNow.AddSeconds(20);
    }

    public async Task ForceContinueAsync(Uri baseUrl)
    {
        IsVerifying = true;
        try
        {
            await AcceptVerifiedChallengeAsync(baseUrl, inPageAlreadyOk: true);
        }
        finally
        {
            IsVerifying = false;
        }
    }

    /// <summary>
    /// Called when the challenge WebView already proved /latest.json is JSON in-page.
    /// Do NOT re-fetch via WebViewAPIClient.FetchAsync here — reparent/gate causes status=0
    /// and false failures (log 11:51:16).
    /// </summary>
    public async Task<bool> CompleteChallengeAsync(Uri baseUrl)
        => await CompleteChallengeAsync(baseUrl, inPageAlreadyOk: false);

    public async Task<bool> CompleteChallengeAsync(Uri baseUrl, bool inPageAlreadyOk)
    {
        IsVerifying = true;
        LastError = null;
        try
        {
            if (inPageAlreadyOk)
            {
                await AcceptVerifiedChallengeAsync(baseUrl, inPageAlreadyOk: true);
                return true;
            }

            // Fallback path (manual button without consecutive probes): soft checks only.
            ApiResponseCache.Clear();
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);
            var hasCf = CookieSessionBridge.HasCloudflareClearance(baseUrl);
            var hasLogin = CookieSessionBridge.HasLoginCookie(baseUrl);
            AppLog.Auth($"CompleteChallenge cookies: cf={hasCf} login={hasLogin}");

            // Prefer trusting CF clearance + in-page state over HttpClient probe
            // (HttpClient TLS still fails CF even with clearance).
            if (hasCf)
            {
                await AcceptVerifiedChallengeAsync(baseUrl, inPageAlreadyOk: false);
                return true;
            }

            LastError = "未检测到 cf_clearance，请完成页面验证";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            IsVerifying = false;
        }
    }

    private async Task AcceptVerifiedChallengeAsync(Uri baseUrl, bool inPageAlreadyOk)
    {
        ApiResponseCache.Clear();
        await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);

        var hasCf = CookieSessionBridge.HasCloudflareClearance(baseUrl);
        var hasLogin = CookieSessionBridge.HasLoginCookie(baseUrl);
        AppLog.Auth(
            $"AcceptVerified: inPageOk={inPageAlreadyOk} cf={hasCf} login={hasLogin}");

        DiscourseAPI.Shared.SetPreferWebView(true);
        DiscourseAPI.Shared.PreferWebViewFirst(TimeSpan.FromMinutes(10)); // enables fallback only
        await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
        DiscourseAPI.Shared.ResetHttpClient();

        // Keep document warm without navigation / extra probe.
        try { await WebViewAPIClient.Shared.RewarmAfterChallengeAsync(baseUrl); }
        catch (Exception ex) { AppLog.Warning("challenge", "rewarm: " + ex.Message); }

        ScheduleCleared();
    }

    private void ScheduleCleared()
    {
        NeedsChallenge = false;
        _isCleared = true;
        // Long suppress: soft-resume refresh must not immediately re-open the sheet.
        _suppressAutoPresentUntil = DateTime.UtcNow.AddSeconds(Math.Max(SuppressIntervalSeconds, 60));
        _presentCount = 0;

        ApiResponseCache.Clear();
        // Quiet window so reparent settles; HttpClient uses cookies without stampede.
        ApiResponseCache.Pause(TimeSpan.FromSeconds(8));
        MessageBusService.Shared.Pause(TimeSpan.FromMinutes(3));
        ImageLoader.Shared.Pause(TimeSpan.FromSeconds(45));
        DiscourseAPI.Shared.PreferWebViewFirst(TimeSpan.FromMinutes(10));

        AppLog.Auth("Challenge cleared — soft resume (HttpClient primary, delayed single refresh)");

        var queue = _uiQueue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000);
                void ResumeUi()
                {
                    try
                    {
                        ApiResponseCache.Resume();
                        // One home refresh only — profile multi-fetch was re-tripping CF.
                        AppEvents.RaiseRefresh();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("challenge", "soft resume UI: " + ex.Message);
                    }
                }

                if (queue is not null)
                    queue.TryEnqueue(ResumeUi);
                else
                    ResumeUi();

                await Task.Delay(30000);
                ImageLoader.Shared.Resume();
                MessageBusService.Shared.Resume();
                if (UserSessionStore.Current.IsLoggedIn &&
                    CookieSessionBridge.HasLoginCookie(DiscourseAPI.Shared.CurrentBaseUrl))
                    UserSessionStore.Current.StartBadgePolling(intervalSeconds: 180);
            }
            catch (Exception ex)
            {
                AppLog.Warning("challenge", "soft resume: " + ex.Message);
            }
        });
    }
}
