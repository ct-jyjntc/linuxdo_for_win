using System.Text.Json;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace LinuxDo.Features.Auth;

/// <summary>
/// Cloudflare challenge sheet — uses the **shared** BridgeWebView (mac parity:
/// adoptVerifiedWebView). Creating a second WebView2 only copies cookies and
/// frequently loses cf_clearance binding.
/// </summary>
public sealed partial class SiteChallengeDialog : ContentDialog
{
    private CancellationTokenSource? _pollCts;
    private bool _finishing;
    private bool _coreReady;
    private int _consecutiveOk;
    private DateTime _openedAt = DateTime.UtcNow;

    private WebView2? _webView;
    private Panel? _originalParent;
    private int _originalIndex = -1;
    private double _savedWidth;
    private double _savedHeight;
    private double _savedOpacity = 1;
    private bool _savedHitTest;
    private HorizontalAlignment _savedHAlign;
    private VerticalAlignment _savedVAlign;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(900);
    private const int MaxPolls = 80;
    // One solid JSON hit is enough; requiring 2 caused "site ready" stuck when
    // the second poll raced a navigation and returned error.
    private const int RequiredConsecutiveOk = 1;
    private int _siteReadyTicks;
    private string _lastProbeDetail = "";

    public SiteChallengeDialog()
    {
        InitializeComponent();
        MessageText.Text = SiteAccessStore.Current.ChallengeMessage;
        Closed += SiteChallengeDialog_Closed;
        Loaded += SiteChallengeDialog_Loaded;
        // Moderate width; page content is fitted via WebViewPageFit CSS.
        try
        {
            double w = 780;
            if (App.Window?.Bounds is { Width: > 0 } b)
                w = Math.Clamp(b.Width * 0.72, 700, 900);
            MinWidth = w;
            MaxWidth = 960;
            Width = w;
        }
        catch
        {
            MinWidth = 760;
            MaxWidth = 960;
            Width = 780;
        }
    }

    private async void SiteChallengeDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                double w = 780;
                if (App.Window?.Bounds is { Width: > 0 } b)
                    w = Math.Clamp(b.Width * 0.72, 700, 900);
                MinWidth = w;
                MaxWidth = 960;
                Width = w;
                if (WebHost is not null)
                {
                    WebHost.ClearValue(FrameworkElement.WidthProperty);
                    WebHost.ClearValue(FrameworkElement.MinWidthProperty);
                    WebHost.MinHeight = 460;
                }
            }
            catch { /* ignore */ }

            SetStatus("正在打开验证页…", busy: true);
            _webView = WebViewAPIClient.Shared.GetWebView();
            if (_webView is null)
            {
                SetStatus("内部浏览器未初始化，请重启应用后再试。", busy: false);
                return;
            }

            // Pause all background WebView API traffic while the same instance is interactive.
            WebViewAPIClient.Shared.SetChallengeUiActive(true);
            ApiResponseCache.Pause(TimeSpan.FromMinutes(3));
            MessageBusService.Shared.Pause(TimeSpan.FromMinutes(3));

            ReparentIntoHost(_webView);
            await WebViewAPIClient.Shared.EnsureInitializedAsync();

            var core = _webView.CoreWebView2;
            if (core is null)
            {
                SetStatus("WebView2 核心未就绪", busy: false);
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            // Re-apply pinned UA only — never re-sample after CF (would invalidate clearance).
            await CookieSessionBridge.PrimeUserAgentFromWebViewAsync(core);
            await WebViewPageFit.InstallDocumentCreatedAsync(core);

            // Avoid external windows during challenge
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.NavigationStarting -= Core_NavigationStarting;
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted -= Core_NavigationCompleted;
            core.NavigationCompleted += Core_NavigationCompleted;

            _coreReady = true;

            // Navigate home for challenge only if not already on-site.
            // Reparenting does NOT require a reload — reloading drops mid-challenge state.
            var baseUrl = AppSettings.Current.BaseUrl;
            var needNav = true;
            try
            {
                var src = _webView.Source;
                if (src is not null &&
                    string.Equals(src.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) &&
                    (src.Scheme == "http" || src.Scheme == "https"))
                {
                    needNav = false;
                }
            }
            catch { /* navigate */ }

            if (needNav)
            {
                _webView.Source = baseUrl;
                SetStatus("请完成下方验证；通过后将自动进入…", busy: true);
            }
            else
            {
                SetStatus("页面已打开，正在检测验证状态…", busy: true);
                await ProbeOnceAsync();
            }

            StartPolling();
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 初始化失败：" + ex.Message, busy: false);
            AppLog.Warning("challenge", "load: " + ex.Message);
        }
    }

    private void SiteChallengeDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        StopPolling();
        try
        {
            if (_webView?.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.NewWindowRequested -= Core_NewWindowRequested;
                _webView.CoreWebView2.NavigationStarting -= Core_NavigationStarting;
                _webView.CoreWebView2.NavigationCompleted -= Core_NavigationCompleted;
            }
        }
        catch { /* ignore */ }

        RestoreWebViewParent();
        WebViewAPIClient.Shared.SetChallengeUiActive(false);
    }

    private void ReparentIntoHost(WebView2 webView)
    {
        // Remember original placement under MainPage
        if (webView.Parent is Panel parent)
        {
            _originalParent = parent;
            _originalIndex = parent.Children.IndexOf(webView);
            parent.Children.Remove(webView);
        }

        _savedWidth = webView.Width;
        _savedHeight = webView.Height;
        _savedOpacity = webView.Opacity;
        _savedHitTest = webView.IsHitTestVisible;
        _savedHAlign = webView.HorizontalAlignment;
        _savedVAlign = webView.VerticalAlignment;

        // Clear off-screen bridge margin or the challenge pane is blank.
        webView.Margin = new Thickness(0);
        webView.Width = double.NaN;
        webView.Height = double.NaN;
        webView.Opacity = 1;
        webView.IsHitTestVisible = true;
        webView.HorizontalAlignment = HorizontalAlignment.Stretch;
        webView.VerticalAlignment = VerticalAlignment.Stretch;
        webView.Visibility = Visibility.Visible;

        WebHost.Child = webView;
    }

    private void RestoreWebViewParent()
    {
        if (_webView is null) return;
        try
        {
            WebHost.Child = null;
        }
        catch { /* ignore */ }

        // Restore off-screen full-size bridge (not 1×1 — Chromium throttles tiny frames).
        _webView.Width = _savedWidth > 100 ? _savedWidth : 960;
        _webView.Height = _savedHeight > 100 ? _savedHeight : 700;
        _webView.Opacity = 0.01;
        _webView.IsHitTestVisible = false;
        _webView.HorizontalAlignment = _savedHAlign;
        _webView.VerticalAlignment = _savedVAlign;
        _webView.Margin = new Thickness(-4000, -4000, 0, 0);

        if (_originalParent is not null)
        {
            try
            {
                if (_originalIndex >= 0 && _originalIndex <= _originalParent.Children.Count)
                    _originalParent.Children.Insert(_originalIndex, _webView);
                else
                    _originalParent.Children.Add(_webView);
            }
            catch
            {
                try { _originalParent.Children.Add(_webView); } catch { /* ignore */ }
            }
        }

        // Keep Shared.Attach pointing at the same instance (parent changed only).
        WebViewAPIClient.Shared.Attach(_webView);
        _webView = null;
        _originalParent = null;
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        try { sender.Navigate(args.Uri); } catch { /* ignore */ }
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_finishing) return;
        _consecutiveOk = 0;
        SetStatus("页面加载中…", busy: true);
    }

    private async void Core_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_finishing) return;
        if (!args.IsSuccess)
        {
            SetStatus("页面加载失败，请检查网络后点「验证完成，继续」", busy: false);
            return;
        }
        var hostW = WebHost.ActualWidth > 100 ? WebHost.ActualWidth : ActualWidth;
        await WebViewPageFit.ApplyAsync(sender, hostW);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(800);
        timer.IsRepeating = false;
        timer.Tick += async (_, __) =>
        {
            try { await WebViewPageFit.ApplyAsync(sender, hostW); } catch { /* ignore */ }
            try { timer.Stop(); } catch { /* ignore */ }
        };
        timer.Start();
        SetStatus("页面已加载，正在自动检测验证状态…", busy: true);
        await ProbeOnceAsync();
        StartPolling();
    }

    private void StartPolling()
    {
        if (_finishing) return;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < MaxPolls && !token.IsCancellationRequested; i++)
            {
                try { await Task.Delay(PollInterval, token); }
                catch { break; }
                try
                {
                    DispatcherQueue.TryEnqueue(async () => await ProbeOnceAsync());
                }
                catch (Exception ex)
                {
                    AppLog.Warning("challenge", "poll: " + ex.Message);
                }
            }
        }, token);
    }

    private void StopPolling()
    {
        try { _pollCts?.Cancel(); } catch { /* ignore */ }
        _pollCts = null;
    }

    private async Task ProbeOnceAsync()
    {
        if (_finishing || !_coreReady || _webView?.CoreWebView2 is null) return;
        if ((DateTime.UtcNow - _openedAt).TotalSeconds < 1.2) return;

        try
        {
            // Cookie-first fast path: Turnstile often sets cf_clearance before latest.json stabilizes.
            try
            {
                await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
            }
            catch { /* ignore */ }

            var page = await EvaluatePageStateAsync();
            if (page == "challenge")
            {
                _consecutiveOk = 0;
                _siteReadyTicks = 0;
                SetStatus("请完成人机验证…", busy: true);
                return;
            }

            var json = await EvaluateLatestJsonAsync();
            AppLog.Network($"challenge probe page={page} latest={json} detail={_lastProbeDetail}");

            if (json == "json")
            {
                _consecutiveOk++;
                _siteReadyTicks = 0;
                SetStatus("验证通过，正在进入…", busy: true);
                if (_consecutiveOk >= RequiredConsecutiveOk)
                    await FinishAsync(fromAuto: true);
                return;
            }

            if (json == "challenge")
            {
                _consecutiveOk = 0;
                _siteReadyTicks = 0;
                SetStatus("站点仍在拦截 API，请继续完成验证…", busy: true);
                return;
            }

            // Soft accept: forum chrome visible + cf_clearance present.
            // latest.json may still return error/html briefly after CF while cookies are good.
            var hasCf = CookieSessionBridge.HasCloudflareClearance(AppSettings.Current.BaseUrl);
            if (page == "site" && hasCf)
            {
                _siteReadyTicks++;
                SetStatus($"页面已就绪（已拿到通行 Cookie）{_siteReadyTicks}/2…", busy: true);
                if (_siteReadyTicks >= 2)
                {
                    AppLog.Auth("Challenge soft-accept: site chrome + cf_clearance");
                    await FinishAsync(fromAuto: true);
                }
                return;
            }

            if (page == "site")
            {
                SetStatus("页面已就绪，等待通行 Cookie / 接口…", busy: true);
            }
            else if (json is "error" or "other" or "html")
            {
                SetStatus($"接口探测：{json}" +
                          (string.IsNullOrEmpty(_lastProbeDetail) ? "" : $"（{_lastProbeDetail}）"),
                    busy: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("challenge", "probe: " + ex.Message);
        }
    }

    private async Task FinishAsync(bool fromAuto)
    {
        if (_finishing) return;
        _finishing = true;
        StopPolling();
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        SetStatus(fromAuto ? "已自动确认验证通过，正在进入…" : "正在完成验证并进入…", busy: true);

        try
        {
            // In-page probe already returned JSON twice — trust that (mac style).
            // Do NOT re-fetch via WebViewAPIClient.FetchAsync (status=0 during reparent).
            await WebViewAPIClient.Shared.AdoptVerifiedAsync(AppSettings.Current.BaseUrl);
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);

            var ok = await SiteAccessStore.Current.CompleteChallengeAsync(
                AppSettings.Current.BaseUrl, inPageAlreadyOk: true);

            if (!ok)
            {
                var err = SiteAccessStore.Current.LastError ?? "接口仍被拦截";
                _finishing = false;
                IsPrimaryButtonEnabled = true;
                IsSecondaryButtonEnabled = true;
                SetStatus($"尚未完全通过：{err} — 请再等几秒或重新完成验证", busy: false);
                StartPolling();
                return;
            }

            SetStatus("验证成功，正在进入…", busy: true);
            Hide();
        }
        catch (Exception ex)
        {
            _finishing = false;
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = true;
            SetStatus("自动进入失败：" + ex.Message + " — 请点「验证完成，继续」", busy: false);
            StartPolling();
        }
    }

    private async void Complete_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        try
        {
            if (_finishing)
            {
                deferral.Complete();
                return;
            }
            _finishing = true;
            StopPolling();
            SetStatus("正在同步 Cookie 并校验…", busy: true);

            // Manual continue: accept if latest.json is JSON OR (site chrome + cf_clearance).
            try
            {
                await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
            }
            catch { /* ignore */ }

            var page = await EvaluatePageStateAsync();
            var pageJson = await EvaluateLatestJsonAsync();
            var hasCf = CookieSessionBridge.HasCloudflareClearance(AppSettings.Current.BaseUrl);
            AppLog.Network($"manual complete page={page} latest={pageJson} cf={hasCf} detail={_lastProbeDetail}");

            var canAccept = pageJson == "json" || (page == "site" && hasCf) || hasCf;
            if (!canAccept)
            {
                args.Cancel = true;
                _finishing = false;
                SetStatus(
                    page == "challenge"
                        ? "仍在人机验证页，请先完成验证"
                        : $"接口尚未放行（{pageJson}），请稍候再试",
                    busy: false);
                StartPolling();
            }
            else
            {
                await WebViewAPIClient.Shared.AdoptVerifiedAsync(AppSettings.Current.BaseUrl);
                await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
                var ok = await SiteAccessStore.Current.CompleteChallengeAsync(
                    AppSettings.Current.BaseUrl, inPageAlreadyOk: true);
                if (!ok)
                {
                    args.Cancel = true;
                    _finishing = false;
                    SetStatus($"验证未完成：{SiteAccessStore.Current.LastError} — 请重试", busy: false);
                    StartPolling();
                }
                else
                {
                    SetStatus("验证成功，正在进入…", busy: true);
                }
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            SetStatus("处理异常：" + ex.Message, busy: false);
            _finishing = false;
            StartPolling();
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void Dismiss_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        StopPolling();
        SiteAccessStore.Current.DismissWithoutClearing();
    }

    private void RefreshPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView is null) return;
            _webView.Margin = new Thickness(0);
            _webView.Opacity = 1;
            _webView.Visibility = Visibility.Visible;
            SetStatus("正在刷新验证页…", busy: true);
            if (_webView.CoreWebView2 is not null)
                _webView.CoreWebView2.Reload();
            else if (AppSettings.Current.BaseUrl is not null)
                _webView.Source = AppSettings.Current.BaseUrl;
        }
        catch (Exception ex)
        {
            SetStatus("刷新失败：" + ex.Message, busy: false);
        }
    }

    private async void FitScale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return;
            var hostW = WebHost.ActualWidth > 100 ? WebHost.ActualWidth : ActualWidth;
            await WebViewPageFit.ApplyAsync(_webView.CoreWebView2, hostW);
            SetStatus("已按窗口缩放页面", busy: false);
        }
        catch (Exception ex)
        {
            SetStatus("缩放失败：" + ex.Message, busy: false);
        }
    }

    private void ReloadHome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView is null) return;
            _webView.Margin = new Thickness(0);
            _webView.Opacity = 1;
            SetStatus("正在打开首页…", busy: true);
            var url = AppSettings.Current.BaseUrl;
            if (_webView.CoreWebView2 is not null)
                _webView.CoreWebView2.Navigate(url.AbsoluteUri);
            else
                _webView.Source = url;
        }
        catch (Exception ex)
        {
            SetStatus("打开失败：" + ex.Message, busy: false);
        }
    }

    private async Task<string> EvaluatePageStateAsync()
    {
        const string script = """
            (function() {
              try {
                var t = (document.title || '').toLowerCase();
                var body = (document.body && document.body.innerText || '').toLowerCase();
                var html = (document.documentElement && document.documentElement.innerHTML || '').toLowerCase();
                if (t.indexOf('just a moment') >= 0) return 'challenge';
                if (t.indexOf('attention required') >= 0) return 'challenge';
                if (t.indexOf('cloudflare') >= 0 && body.indexOf('verify') >= 0) return 'challenge';
                if (body.indexOf('checking your browser') >= 0) return 'challenge';
                if (body.indexOf('verify you are human') >= 0) return 'challenge';
                if (body.indexOf('needs to review the security') >= 0) return 'challenge';
                if (body.indexOf('确认您是真人') >= 0 || body.indexOf('正在验证') >= 0) return 'challenge';
                if (document.querySelector('#challenge-form, #cf-challenge-running, .cf-browser-verification, #challenge-stage, #turnstile-wrapper, iframe[src*="challenges.cloudflare.com"]')) {
                  return 'challenge';
                }
                if (html.indexOf('challenge-platform') >= 0 && !document.querySelector('#main-outlet, .topic-list')) {
                  return 'challenge';
                }
                if (document.querySelector('#main-outlet, .topic-list, .list-container, #site-logo, .d-header .title')) {
                  return 'site';
                }
                return 'unknown';
              } catch (e) {
                return 'error';
              }
            })()
            """;
        return await EvalStringAsync(script);
    }

    private async Task<string> EvaluateLatestJsonAsync()
    {
        _lastProbeDetail = "";
        // Return "status|kind" so we can log why it failed (200 html vs 403 vs network).
        const string script = """
            (async () => {
              try {
                // Prefer absolute URL on current origin — relative can fail on some CF interstitial paths.
                const u = (location.origin || '') + '/latest.json';
                const resp = await fetch(u, {
                  credentials: 'include',
                  headers: {
                    'Accept': 'application/json, text/javascript, */*; q=0.01',
                    'X-Requested-With': 'XMLHttpRequest',
                    'Discourse-Present': 'true'
                  },
                  cache: 'no-store',
                  mode: 'cors',
                  redirect: 'follow'
                });
                const text = await resp.text();
                const head = (text || '').trim().slice(0, 120).toLowerCase();
                if (resp.status === 403 || resp.status === 503) return resp.status + '|challenge';
                if (head.indexOf('just a moment') >= 0) return resp.status + '|challenge';
                if (head.indexOf('cf-browser-verification') >= 0) return resp.status + '|challenge';
                if (head.indexOf('cf-mitigated') >= 0) return resp.status + '|challenge';
                if (head.startsWith('{') || head.startsWith('[')) return resp.status + '|json';
                if (head.startsWith('<!doctype') || head.startsWith('<html')) return resp.status + '|html';
                return resp.status + '|other';
              } catch (e) {
                return '0|error:' + String(e && e.message ? e.message : e).slice(0, 80);
              }
            })()
            """;
        var raw = await EvalStringAsync(script);
        // Parse "200|json" / "403|challenge" / "0|error:..."
        var pipe = raw.IndexOf('|');
        if (pipe > 0)
        {
            var statusPart = raw[..pipe];
            var kind = raw[(pipe + 1)..];
            _lastProbeDetail = statusPart;
            if (kind.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                _lastProbeDetail = kind;
                return "error";
            }
            return kind switch
            {
                "json" => "json",
                "challenge" => "challenge",
                "html" => "html",
                _ => "other"
            };
        }
        // Backward-compatible plain tokens
        return raw switch
        {
            "json" or "challenge" or "html" or "error" or "other" => raw,
            _ => "error"
        };
    }

    private async Task<string> EvalStringAsync(string script)
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return "error";
            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw == "null") return "error";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind switch
                {
                    JsonValueKind.String => doc.RootElement.GetString() ?? "error",
                    _ => doc.RootElement.GetRawText().Trim('"')
                };
            }
            catch
            {
                return raw.Trim().Trim('"');
            }
        }
        catch (Exception ex)
        {
            _lastProbeDetail = ex.Message;
            return "error";
        }
    }

    private void SetStatus(string text, bool busy)
    {
        StatusText.Text = text;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
