using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public partial class SiteAccessStore : ObservableObject
{
    public static SiteAccessStore Current { get; } = new();

    [ObservableProperty] private bool _needsChallenge;
    [ObservableProperty] private string _challengeMessage =
        "LinuxDo 启用了 Cloudflare 防护，需要在浏览器视图中完成一次验证。";
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string? _lastError;

    private bool _isCleared;
    private DateTime? _suppressAutoPresentUntil;
    private int _presentCount;
    private const double SuppressIntervalSeconds = 12;

    public bool IsCleared => _isCleared;

    public void PresentChallenge(string? message = null, bool force = false)
    {
        if (!force)
        {
            if (_suppressAutoPresentUntil is not null && DateTime.UtcNow < _suppressAutoPresentUntil)
                return;
            if (NeedsChallenge) return;
            if (_presentCount > 4) return;
        }
        else
        {
            _suppressAutoPresentUntil = null;
            _isCleared = false;
            if (NeedsChallenge) NeedsChallenge = false;
        }

        if (message is not null) ChallengeMessage = message;
        NeedsChallenge = true;
        if (force) _presentCount = 0;
        else _presentCount++;
        LastError = null;
        IsVerifying = false;
    }

    public void HandleApiError(Exception error)
    {
        if (error is APIError api && api.IsChallengeRelated)
            PresentChallenge(api.Message);
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
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);
            DiscourseAPI.Shared.SetPreferWebView(true);
            await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
            DiscourseAPI.Shared.ResetHttpClient();
            await WebViewAPIClient.Shared.RewarmAfterChallengeAsync(baseUrl);
            MarkCleared();
        }
        finally
        {
            IsVerifying = false;
        }
    }

    public async Task<bool> CompleteChallengeAsync(Uri baseUrl)
    {
        IsVerifying = true;
        LastError = null;
        try
        {
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);
            WebViewAPIClient.Shared.MarkNeedsWarmUp();

            // Bound wait so the Primary button never feels "dead".
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            try
            {
                await WebViewAPIClient.Shared.PrepareAsync(baseUrl).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                LastError = "准备 WebView 超时，将尝试强制继续";
                return false;
            }

            byte[]? data;
            try
            {
                data = await WebViewAPIClient.Shared.FetchAsync(
                    new Uri(baseUrl, "latest.json"), "GET", expectJson: true)
                    .WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                LastError = "校验超时，将尝试强制继续";
                return false;
            }

            if (data is null || data.Length == 0 ||
                !ResponseInspector.IsProbablyJson(data) ||
                ResponseInspector.LooksLikeCloudflare(data, 200))
            {
                LastError = "仍未通过验证：接口返回了防护页";
                return false;
            }
            DiscourseAPI.Shared.SetPreferWebView(true);
            await DiscourseAPI.Shared.AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
            DiscourseAPI.Shared.ResetHttpClient();
            MarkCleared();
            return true;
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

    private void MarkCleared()
    {
        NeedsChallenge = false;
        _isCleared = true;
        _suppressAutoPresentUntil = DateTime.UtcNow.AddSeconds(SuppressIntervalSeconds);
        _presentCount = 0;
        AppEvents.RaiseRefresh();
    }
}
