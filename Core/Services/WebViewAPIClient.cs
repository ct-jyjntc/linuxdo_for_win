using System.Text;
using System.Text.Json;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace LinuxDo.Core.Services;

/// <summary>
/// Single shared WebView2 for Cloudflare challenge UI + API fetch (mac parity).
/// CF clearance is bound to that browser context — a second WebView + cookie copy is not enough.
/// Must be attached to the visual tree before use.
/// </summary>
public sealed class WebViewAPIClient
{
    public static WebViewAPIClient Shared { get; } = new();

    private WebView2? _webView;
    private CoreWebView2? _core;
    private bool _isWarm;
    private Uri? _warmBase;
    private bool _initHooksDone;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while challenge UI has reparented / is using the bridge WebView.</summary>
    public bool IsChallengeUiActive { get; private set; }

    /// <summary>True when the shared document is considered warm on-site (no navigation needed).</summary>
    public bool IsWarmDocument => _isWarm;

    public void Attach(WebView2 webView)
    {
        _webView = webView;
        // If Core was already created (re-attach after reparent), refresh pointer.
        if (webView.CoreWebView2 is not null)
            _core = webView.CoreWebView2;
    }

    public WebView2? GetWebView() => _webView;

    public async Task EnsureInitializedAsync()
    {
        if (_webView is null)
            throw new InvalidOperationException("WebView2 not attached. Call Attach from MainWindow first.");

        if (_core is null || _webView.CoreWebView2 is null)
        {
            await _webView.EnsureCoreWebView2Async();
            _core = _webView.CoreWebView2;
        }
        else
        {
            _core = _webView.CoreWebView2;
        }

        if (_initHooksDone && _core is not null) return;

        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        // Hide "webdriver"/automation tells where possible and pin a real browser UA.
        try { _core.Settings.IsReputationCheckingRequired = false; } catch { /* older runtime */ }
        try { _core.Settings.AreBrowserAcceleratorKeysEnabled = true; } catch { /* ignore */ }
        CookieSessionBridge.PrimeUserAgent();
        await CookieSessionBridge.PrimeUserAgentFromWebViewAsync(_core);
        _core.Settings.UserAgent = CookieSessionBridge.UserAgent;
        try
        {
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(
                "Object.defineProperty(navigator,'webdriver',{get:()=>undefined});");
        }
        catch { /* ignore */ }

        // When the document is navigating / unloading, in-page fetch aborts with status 0.
        _core.NavigationStarting += (_, _) => { _isWarm = false; };
        _initHooksDone = true;
    }

    /// <summary>
    /// Mac: adoptVerifiedWebView — keep the verified challenge document + cookie jar.
    /// Do not reload a fresh page after the user already passed CF.
    /// </summary>
    public async Task AdoptVerifiedAsync(Uri baseUrl)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            if (_core is null || _webView is null) return;

            // Do NOT rewrite User-Agent here — CF clearance is bound to the UA that solved Turnstile.
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);

            var origin = new Uri(baseUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
            // Always keep the verified document. Soft probe only.
            try
            {
                var probe = await ExecuteFetchInPageAsync(
                    new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/latest.json"),
                    "GET",
                    new Dictionary<string, string>
                    {
                        ["Accept"] = CookieSessionBridge.AcceptJson,
                        ["X-Requested-With"] = "XMLHttpRequest"
                    },
                    bodyJson: null,
                    expectJson: true);
                if (ResponseInspector.IsProbablyJson(probe) &&
                    !ResponseInspector.LooksLikeCloudflare(probe, 200))
                {
                    _isWarm = true;
                    _warmBase = origin;
                    AppLog.Network("Adopted verified challenge WebView for API fetch (no reload)");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("webview", "adopt probe soft-fail (still keep page): " + ex.Message);
            }

            // Even if probe fails, KEEP the page — do not mark cold (that caused rewarm navigate).
            _isWarm = true;
            _warmBase = origin;
            AppLog.Network("Adopted challenge WebView (keep document; HttpClient has cookies)");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetChallengeUiActive(bool active) => IsChallengeUiActive = active;

    public async Task EnsureWarmAsync(Uri baseUrl, double timeoutSeconds = 3)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureWarmCoreAsync(baseUrl, timeoutSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Warm without taking the gate — caller must already hold _gate.</summary>
    private async Task EnsureWarmCoreAsync(Uri baseUrl, double timeoutSeconds)
    {
        await EnsureInitializedAsync();
        if (_webView is null || _core is null) return;
        if (IsChallengeUiActive)
        {
            AppLog.Network("EnsureWarm skipped — challenge UI active");
            return;
        }

        var origin = new Uri(baseUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
        var forumHost = DiscourseAPI.Shared.CurrentBaseUrl.Host;

        // Hard guard: never navigate the shared bridge off the forum host.
        // Connect/other sites used to call EnsureWarm(connect.linux.do) and wipe CF context.
        if (!string.IsNullOrEmpty(forumHost) &&
            !string.Equals(origin.Host, forumHost, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warning("webview", $"Refuse warm navigate to foreign host {origin.Host} (forum={forumHost})");
            return;
        }

        // If already on the right origin, never navigate (even if _isWarm was cleared by reparent).
        if (await IsDocumentUsableAsync() && await IsSameOriginAsync(origin))
        {
            _isWarm = true;
            _warmBase = origin;
            return;
        }

        if (_isWarm && _warmBase is not null &&
            string.Equals(_warmBase.GetLeftPart(UriPartial.Authority),
                origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _core.NavigationCompleted -= Handler;
            tcs.TrySetResult(args.IsSuccess);
        }
        _core.NavigationCompleted += Handler;
        try
        {
            AppLog.Network($"WebView warm → {origin}");
            _webView.Source = origin;
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completed == tcs.Task && await tcs.Task)
            {
                await Task.Delay(250);
                _isWarm = true;
                _warmBase = origin;
            }
            else
            {
                _core.NavigationCompleted -= Handler;
                if (await IsDocumentUsableAsync())
                {
                    _isWarm = true;
                    _warmBase = origin;
                }
            }
        }
        catch
        {
            try { _core.NavigationCompleted -= Handler; } catch { /* ignore */ }
        }
    }

    private async Task<bool> IsDocumentUsableAsync()
    {
        if (_core is null) return false;
        try
        {
            var raw = await _core.ExecuteScriptAsync(
                "(() => { try { const h = location.hostname || ''; const p = location.protocol || ''; " +
                "return (p === 'http:' || p === 'https:') && h.length > 0 ? '1' : '0'; } catch(e) { return '0'; } })()");
            var s = UnwrapScriptResult(raw).Trim().Trim('"');
            return s == "1";
        }
        catch
        {
            return false;
        }
    }

    public void MarkNeedsWarmUp()
    {
        _isWarm = false;
    }

    /// <summary>
    /// Mac rewarmAfterChallenge: prefer already-warm adopted view; only reload if cold/CF.
    /// CRITICAL: never navigate just to "warm" — a full reload after CF can drop clearance
    /// or thrash the same document that just passed Turnstile (log: adopt then "WebView warm →").
    /// </summary>
    public async Task RewarmAfterChallengeAsync(Uri baseUrl)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(baseUrl, force: true);

            var origin = new Uri(baseUrl.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
            var onSite = await IsDocumentUsableAsync() &&
                         await IsSameOriginAsync(origin);

            if (onSite && !await PageLooksLikeCloudflareAsync())
            {
                // In-page probe only — no navigation.
                try
                {
                    var data = await ExecuteFetchInPageAsync(
                        new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/latest.json"),
                        "GET",
                        new Dictionary<string, string>
                        {
                            ["Accept"] = CookieSessionBridge.AcceptJson,
                            ["X-Requested-With"] = "XMLHttpRequest"
                        },
                        null,
                        expectJson: true);
                    if (ResponseInspector.IsProbablyJson(data) &&
                        !ResponseInspector.LooksLikeCloudflare(data, 200))
                    {
                        _isWarm = true;
                        _warmBase = origin;
                        AppLog.Network("Post-challenge WebView already warm (no reload)");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Document may still be good for future fetch; HttpClient has cookies.
                    AppLog.Warning("webview", "post-challenge probe soft-fail (keep page): " + ex.Message);
                    _isWarm = true;
                    _warmBase = origin;
                    return;
                }

                // On-site but latest not JSON — keep page, let HttpClient work with cookies.
                _isWarm = true;
                _warmBase = origin;
                AppLog.Network("Post-challenge: keep current document (no forced reload)");
                return;
            }

            // Only navigate when we are not on the site at all (about:blank / wrong host).
            if (!onSite)
            {
                AppLog.Network("Post-challenge: document not on-site — warm navigate once");
                _isWarm = false;
                await EnsureWarmCoreAsync(baseUrl, 8);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> IsSameOriginAsync(Uri origin)
    {
        if (_core is null) return false;
        try
        {
            var raw = await _core.ExecuteScriptAsync(
                "(() => { try { return location.origin || ''; } catch(e) { return ''; } })()");
            var s = UnwrapScriptResult(raw).Trim().Trim('"');
            return !string.IsNullOrEmpty(s) &&
                   string.Equals(s, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task PrepareAsync(Uri baseUrl)
    {
        // Don't navigate away while user is solving CF in the same WebView.
        if (IsChallengeUiActive)
        {
            AppLog.Network("Prepare skipped — challenge UI active on shared WebView");
            return;
        }
        await EnsureWarmAsync(baseUrl, 5);
    }

    private async Task<bool> PageLooksLikeCloudflareAsync()
    {
        if (_core is null) return false;
        try
        {
            var raw = await _core.ExecuteScriptAsync(
                """
                (() => {
                  try {
                    var t = (document.title||'').toLowerCase();
                    var body = (document.body && document.body.innerText || '').toLowerCase();
                    if (t.indexOf('just a moment')>=0 || t.indexOf('attention required')>=0) return '1';
                    if (document.querySelector('#challenge-form,#cf-challenge-running,.cf-browser-verification,#challenge-stage,iframe[src*="challenges.cloudflare.com"]')) return '1';
                    if (body.indexOf('verify you are human')>=0 || body.indexOf('确认您是真人')>=0) return '1';
                    return '0';
                  } catch(e) { return '0'; }
                })()
                """);
            return UnwrapScriptResult(raw).Trim().Trim('"') == "1";
        }
        catch { return false; }
    }

    public async Task<byte[]> FetchAsync(
        Uri url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        string? bodyJson = null,
        bool allowWarmUp = true,
        bool expectJson = true,
        int timeoutSeconds = 28)
    {
        // Outer budget includes queue wait on the single WebView gate.
        // Work timeout (after gate) is slightly shorter so we fail cleanly instead of hanging.
        var overallSeconds = Math.Max(timeoutSeconds + 12, 30);
        using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(overallSeconds));
        try
        {
            return await FetchCoreAsync(
                    url, method, headers, bodyJson, allowWarmUp, expectJson,
                    workTimeoutSeconds: timeoutSeconds,
                    overallCts.Token)
                .WaitAsync(overallCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning("webview", $"fetch timed out overall={overallSeconds}s work={timeoutSeconds}s {method} {url}");
            throw new APIError.Network(new TimeoutException(
                $"WebView fetch timed out after {timeoutSeconds}s: {url}"));
        }
    }

    private async Task<byte[]> FetchCoreAsync(
        Uri url,
        string method,
        Dictionary<string, string>? headers,
        string? bodyJson,
        bool allowWarmUp,
        bool expectJson,
        int workTimeoutSeconds,
        CancellationToken ct)
    {
        if (IsChallengeUiActive)
        {
            // Never run background API fetch through the WebView while the user is on the CF page.
            throw new APIError.Network(new Exception("challenge UI active — defer WebView fetch"));
        }

        // Wait for the exclusive WebView gate separately so one slow warm doesn't
        // burn the entire budget for every queued caller (was the main "cancel/timeout" spam).
        var gateWait = Math.Max(8, workTimeoutSeconds);
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(gateWait), ct))
        {
            AppLog.Warning("webview", $"gate busy >{gateWait}s for {method} {url}");
            throw new APIError.Network(new TimeoutException(
                $"WebView fetch timed out after {workTimeoutSeconds}s (queue): {url}"));
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await EnsureInitializedAsync();
            // IMPORTANT: do not call EnsureWarmAsync here (it also takes _gate → deadlock).
            // Only warm-navigate if document is blank/wrong-host — and only for forum host.
            if (allowWarmUp &&
                Uri.TryCreate(url.GetLeftPart(UriPartial.Authority), UriKind.Absolute, out var origin))
            {
                var forumHost = DiscourseAPI.Shared.CurrentBaseUrl.Host;
                var target = new Uri(origin.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
                if (!string.IsNullOrEmpty(forumHost) &&
                    !string.Equals(target.Host, forumHost, StringComparison.OrdinalIgnoreCase))
                {
                    // Cross-host request (e.g. connect) must not move the bridge document.
                    throw new APIError.Network(new Exception(
                        $"refuse WebView fetch for foreign host {target.Host}"));
                }

                var onSite = await IsDocumentUsableAsync() && await IsSameOriginAsync(target);
                if (!onSite)
                {
                    try { await EnsureWarmCoreAsync(origin, 4); }
                    catch { /* best effort */ }
                }
                else
                {
                    _isWarm = true;
                    _warmBase = target;
                }
            }

            if (_core is null) throw new APIError.InvalidResponse();

            using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            workCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, workTimeoutSeconds)));

            // Single attempt by default — retry+rewarm was thrashing CF after adopt.
            try
            {
                return await ExecuteFetchInPageAsync(url, method, headers, bodyJson, expectJson)
                    .WaitAsync(workCts.Token);
            }
            catch (APIError.Network nex) when (!nex.Message.Contains("challenge UI", StringComparison.Ordinal))
            {
                // One soft retry without navigation.
                AppLog.Warning("webview", $"fetch soft-retry (no navigate): {url}");
                await Task.Delay(250, workCts.Token);
                return await ExecuteFetchInPageAsync(url, method, headers, bodyJson, expectJson)
                    .WaitAsync(workCts.Token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> ExecuteFetchInPageAsync(
        Uri url,
        string method,
        Dictionary<string, string>? headers,
        string? bodyJson,
        bool expectJson)
    {
        if (_core is null) throw new APIError.InvalidResponse();

        var headerObj = new Dictionary<string, string>(headers ?? new());
        // Mirror a real Chromium XHR/fetch from linux.do pages
        if (!headerObj.ContainsKey("Accept"))
            headerObj["Accept"] = expectJson
                ? CookieSessionBridge.AcceptJson
                : CookieSessionBridge.AcceptHtml;
        if (!headerObj.ContainsKey("Accept-Language"))
            headerObj["Accept-Language"] = CookieSessionBridge.AcceptLanguage;
        if (!headerObj.ContainsKey("X-Requested-With"))
            headerObj["X-Requested-With"] = "XMLHttpRequest";
        if (!headerObj.ContainsKey("Discourse-Present"))
            headerObj["Discourse-Present"] = "true";
        if (!headerObj.ContainsKey("sec-ch-ua"))
            headerObj["sec-ch-ua"] = CookieSessionBridge.SecChUa;
        if (!headerObj.ContainsKey("sec-ch-ua-mobile"))
            headerObj["sec-ch-ua-mobile"] = CookieSessionBridge.SecChUaMobile;
        if (!headerObj.ContainsKey("sec-ch-ua-platform"))
            headerObj["sec-ch-ua-platform"] = CookieSessionBridge.SecChUaPlatform;
        if (!headerObj.ContainsKey("Sec-Fetch-Site"))
            headerObj["Sec-Fetch-Site"] = "same-origin";
        if (!headerObj.ContainsKey("Sec-Fetch-Mode"))
            headerObj["Sec-Fetch-Mode"] = "cors";
        if (!headerObj.ContainsKey("Sec-Fetch-Dest"))
            headerObj["Sec-Fetch-Dest"] = "empty";

        var headerJson = JsonSerializer.Serialize(headerObj);
        var bodyLiteral = bodyJson is null
            ? "null"
            : JsonSerializer.Serialize(bodyJson);
        var targetOrigin = url.GetLeftPart(UriPartial.Authority);

        var script = $$"""
            (async () => {
              try {
                // Refuse to fetch from about:blank / wrong origin — causes status 0 / CORS noise.
                const targetOrigin = {{JsonSerializer.Serialize(targetOrigin)}};
                if (!location.protocol || location.protocol === 'about:' ||
                    (location.origin !== 'null' && location.origin !== targetOrigin &&
                     location.hostname && location.hostname.length > 0 &&
                     !targetOrigin.includes(location.hostname))) {
                  return JSON.stringify({
                    ok: false, status: 0, error: true,
                    body: 'WebView not on site origin (' + (location.href || 'blank') + ')'
                  });
                }
                const headers = {{headerJson}};
                const init = {
                  method: {{JsonSerializer.Serialize(method)}},
                  headers,
                  credentials: 'include',
                  mode: 'cors',
                  cache: 'no-store',
                  redirect: 'follow',
                  referrer: location.href,
                  referrerPolicy: 'strict-origin-when-cross-origin'
                };
                const bodyRaw = {{bodyLiteral}};
                if (bodyRaw !== null && bodyRaw !== undefined) {
                  init.body = typeof bodyRaw === 'string' ? bodyRaw : JSON.stringify(bodyRaw);
                }
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), 15000);
                init.signal = controller.signal;
                try {
                  const resp = await fetch({{JsonSerializer.Serialize(url.AbsoluteUri)}}, init);
                  const text = await resp.text();
                  clearTimeout(timer);
                  return JSON.stringify({
                    ok: resp.ok,
                    status: resp.status,
                    body: text,
                    contentType: resp.headers.get('content-type') || ''
                  });
                } catch (e) {
                  clearTimeout(timer);
                  throw e;
                }
              } catch (e) {
                return JSON.stringify({ ok: false, status: 0, body: String(e), error: true });
              }
            })()
            """;

        var result = await _core.ExecuteScriptAsync(script);
        var unquoted = UnwrapScriptResult(result);
        if (string.IsNullOrWhiteSpace(unquoted) || unquoted is "null" or "undefined")
            throw new APIError.Network(new Exception("WebView returned empty fetch result"));

        using var doc = JsonDocument.Parse(unquoted);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var st)
            ? (st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0)
            : 0;
        // body is normally a JSON string; tolerate non-string (object/array) from edge cases
        string body;
        if (root.TryGetProperty("body", out var b))
        {
            body = b.ValueKind switch
            {
                JsonValueKind.String => b.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => b.GetRawText()
            };
        }
        else
        {
            body = "";
        }
        var data = Encoding.UTF8.GetBytes(body);
        var isError = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True;

        // status 0 = browser network failure / abort / not-on-origin — never surface as "HTTP 0"
        if (isError || status == 0)
        {
            var detail = string.IsNullOrWhiteSpace(body) ? "connection aborted" : body;
            AppLog.Warning("webview", $"fetch status=0: {detail}");
            throw new APIError.Network(new Exception(
                detail.Contains("not on site origin", StringComparison.OrdinalIgnoreCase)
                    ? "页面尚未就绪，正在重试…"
                    : $"网络中断：{TrimMsg(detail)}"));
        }

        if (ResponseInspector.LooksLikeCloudflare(data, status))
            throw new APIError.CloudflareChallenge();

        if (status is >= 200 and <= 299)
        {
            if (expectJson && ResponseInspector.LooksLikeHtml(data) && !ResponseInspector.IsProbablyJson(data))
                throw new APIError.NonJsonResponse();
            return data;
        }

        throw status switch
        {
            401 => new APIError.Unauthorized(),
            403 => new APIError.Forbidden(),
            429 => new APIError.RateLimited(),
            _ => new APIError.Http(status, body.Length > 200 ? body[..200] : body)
        };
    }

    private static string TrimMsg(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    public async Task<string?> ExtractCsrfTokenAsync(Uri baseUrl)
    {
        try
        {
            await EnsureInitializedAsync();
            await EnsureWarmAsync(baseUrl, 4);
            if (_core is null) return null;

            var script = """
                (function() {
                  try {
                    var meta = document.querySelector('meta[name="csrf-token"]');
                    if (meta) {
                      var c = meta.getAttribute('content') || '';
                      if (c) return c;
                    }
                    if (window.csrfToken) return String(window.csrfToken);
                    try {
                      if (window.Discourse && Discourse.Session && Discourse.Session.current) {
                        var s = Discourse.Session.current();
                        if (s && s.csrfToken) return String(s.csrfToken);
                      }
                    } catch (e1) {}
                    try {
                      var pre = document.getElementById('data-preloaded');
                      if (pre) {
                        var raw = pre.getAttribute('data-preloaded') || pre.textContent || '';
                        var m = raw.match(/"csrf[^"]*"\s*:\s*"([^"]+)"/i);
                        if (m && m[1]) return m[1];
                      }
                    } catch (e2) {}
                  } catch (e) {}
                  return '';
                })()
                """;
            var result = await _core.ExecuteScriptAsync(script);
            var token = UnwrapScriptResult(result);
            // UnwrapScriptResult may leave surrounding quotes stripped; empty means none
            if (string.IsNullOrEmpty(token) || token is "null" or "undefined") return null;
            // If still JSON-quoted, strip once more
            if (token.Length >= 2 && token[0] == '"')
            {
                try { token = JsonSerializer.Deserialize<string>(token) ?? token; }
                catch { /* keep */ }
            }
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            AppLog.Warning("webview", "CSRF extract: " + ex.Message);
            return null;
        }
    }

    public async Task ClearCookiesAsync(Uri baseUrl)
    {
        try
        {
            await EnsureInitializedAsync();
            if (_core is null) return;
            var cookies = await _core.CookieManager.GetCookiesAsync(baseUrl.AbsoluteUri);
            foreach (var c in cookies)
                _core.CookieManager.DeleteCookie(c);
            MarkNeedsWarmUp();
        }
        catch (Exception ex)
        {
            AppLog.Warning("webview", "Clear cookies: " + ex.Message);
        }
    }

    public async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(Uri baseUrl)
    {
        await EnsureInitializedAsync();
        if (_core is null) return [];
        return await _core.CookieManager.GetCookiesAsync(baseUrl.AbsoluteUri);
    }

    /// <summary>
    /// ExecuteScriptAsync returns a JSON-encoded value (usually a quoted string).
    /// When the script itself returns JSON.stringify(...), we get a double-encoded string.
    /// Sometimes WebView returns non-string JSON (object/number) — don't throw.
    /// </summary>
    private static string UnwrapScriptResult(string? result)
    {
        if (string.IsNullOrEmpty(result) || result == "null") return "";
        try
        {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString() ?? "",
                JsonValueKind.Null => "",
                // Already an object/array — use raw JSON text
                JsonValueKind.Object or JsonValueKind.Array => doc.RootElement.GetRawText(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => doc.RootElement.GetRawText(),
                _ => result
            };
        }
        catch
        {
            // Not valid JSON — return as-is
            return result;
        }
    }
}
