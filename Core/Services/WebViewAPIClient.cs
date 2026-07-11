using System.Text;
using System.Text.Json;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace LinuxDo.Core.Services;

/// <summary>
/// Hidden WebView2 used for Cloudflare-sensitive fetches and CSRF extraction.
/// Must be attached to the visual tree before use.
/// </summary>
public sealed class WebViewAPIClient
{
    public static WebViewAPIClient Shared { get; } = new();

    private WebView2? _webView;
    private CoreWebView2? _core;
    private bool _isWarm;
    private Uri? _warmBase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Attach(WebView2 webView)
    {
        _webView = webView;
    }

    public WebView2? GetWebView() => _webView;

    public async Task EnsureInitializedAsync()
    {
        if (_core is not null) return;
        if (_webView is null)
            throw new InvalidOperationException("WebView2 not attached. Call Attach from MainWindow first.");

        await _webView.EnsureCoreWebView2Async();
        _core = _webView.CoreWebView2;
        _core.Settings.UserAgent = CookieSessionBridge.UserAgent;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        CookieSessionBridge.UserAgent = _core.Settings.UserAgent;
    }

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
        if (_isWarm && _warmBase == baseUrl) return;
        if (_webView is null || _core is null) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _core.NavigationCompleted -= Handler;
            tcs.TrySetResult(args.IsSuccess);
        }
        _core.NavigationCompleted += Handler;
        try
        {
            _webView.Source = baseUrl;
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completed == tcs.Task && await tcs.Task)
            {
                _isWarm = true;
                _warmBase = baseUrl;
            }
            else
            {
                _core.NavigationCompleted -= Handler;
            }
        }
        catch
        {
            try { _core.NavigationCompleted -= Handler; } catch { /* ignore */ }
        }
    }

    public void MarkNeedsWarmUp()
    {
        _isWarm = false;
    }

    public async Task RewarmAfterChallengeAsync(Uri baseUrl)
    {
        MarkNeedsWarmUp();
        await EnsureWarmAsync(baseUrl, 5);
    }

    public async Task PrepareAsync(Uri baseUrl)
    {
        await EnsureWarmAsync(baseUrl, 5);
    }

    public async Task<byte[]> FetchAsync(
        Uri url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        string? bodyJson = null,
        bool allowWarmUp = true,
        bool expectJson = true,
        int timeoutSeconds = 20)
    {
        // Hard timeout so UI never spins forever if WebView stalls.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await FetchCoreAsync(url, method, headers, bodyJson, allowWarmUp, expectJson)
                .WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new APIError.Network(new TimeoutException($"WebView fetch timed out after {timeoutSeconds}s: {url}"));
        }
    }

    private async Task<byte[]> FetchCoreAsync(
        Uri url,
        string method,
        Dictionary<string, string>? headers,
        string? bodyJson,
        bool allowWarmUp,
        bool expectJson)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureInitializedAsync();
            // IMPORTANT: do not call EnsureWarmAsync here (it also takes _gate → deadlock).
            if (allowWarmUp && !_isWarm &&
                Uri.TryCreate(url.GetLeftPart(UriPartial.Authority), UriKind.Absolute, out var origin))
            {
                try { await EnsureWarmCoreAsync(origin, 3); }
                catch { /* best effort */ }
            }

            if (_core is null) throw new APIError.InvalidResponse();

            var headerObj = new Dictionary<string, string>(headers ?? new());
            if (!headerObj.ContainsKey("Accept"))
                headerObj["Accept"] = expectJson ? "application/json" : "*/*";

            var headerJson = JsonSerializer.Serialize(headerObj);
            var bodyLiteral = bodyJson is null
                ? "null"
                : JsonSerializer.Serialize(bodyJson);

            var script = $$"""
                (async () => {
                  try {
                    const headers = {{headerJson}};
                    const init = { method: {{JsonSerializer.Serialize(method)}}, headers, credentials: 'include' };
                    const bodyRaw = {{bodyLiteral}};
                    if (bodyRaw !== null && bodyRaw !== undefined) {
                      init.body = typeof bodyRaw === 'string' ? bodyRaw : JSON.stringify(bodyRaw);
                    }
                    const controller = new AbortController();
                    const timer = setTimeout(() => controller.abort(), 18000);
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

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
                throw new APIError.Network(new Exception(body));

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
        finally
        {
            _gate.Release();
        }
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
                  const meta = document.querySelector('meta[name="csrf-token"]');
                  if (meta) return meta.getAttribute('content') || '';
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
