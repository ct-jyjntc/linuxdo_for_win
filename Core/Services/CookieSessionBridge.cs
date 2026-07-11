using System.Net;
using System.Text.RegularExpressions;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>
/// Shared cookie jar between WebView2 and HttpClient so web login works for API calls.
/// User-Agent must look like a real desktop browser: Cloudflare binds cf_clearance to the
/// exact UA (+ client hints). A custom "LinuxDo-Windows" token is frequently treated as a bot.
/// </summary>
public static partial class CookieSessionBridge
{
    public static readonly CookieContainer CookieJar = new();
    private static readonly object SyncGate = new();
    private static DateTime? _lastSyncAt;
    private static string? _userAgent;
    private static string? _secChUa;
    private static string? _secChUaFull;
    private static string? _secChUaPlatform;
    private static bool _primedFromWebView;

    /// <summary>
    /// Realistic Chromium-on-Windows fallback (Edge-flavored, matches WebView2 family).
    /// Prefer the live WebView2 navigator.userAgent once primed.
    /// </summary>
    // Prefer plain Chrome (not Edg/) for fallback — WebView2 is Chromium; Edge brand
    // mismatch with some CF rules was observed more often than plain Chrome strings.
    public static string FallbackChromeUserAgent { get; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/138.0.0.0 Safari/537.36";

    public static string UserAgent
    {
        get
        {
            if (!string.IsNullOrEmpty(_userAgent)) return _userAgent!;
            return FallbackChromeUserAgent;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _userAgent = value.Trim();
            DeriveClientHintsFromUserAgent(_userAgent);
        }
    }

    public static string SecChUa =>
        _secChUa ?? "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\", \"Microsoft Edge\";v=\"138\"";

    public static string SecChUaMobile => "?0";

    public static string SecChUaPlatform =>
        _secChUaPlatform ?? "\"Windows\"";

    public static string SecChUaFullVersionList =>
        _secChUaFull
        ?? "\"Not)A;Brand\";v=\"10.0.0.0\", \"Chromium\";v=\"138.0.7204.183\", \"Microsoft Edge\";v=\"138.0.3351.109\"";

    public static string AcceptLanguage => "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7";

    public static string AcceptHtml =>
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

    public static string AcceptJson =>
        "application/json, text/javascript, */*; q=0.01";

    public static string AcceptImage =>
        "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";

    public static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            CookieContainer = CookieJar,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        ApplyDefaultHttpClientHeaders(client);
        return client;
    }

    public static void ApplyDefaultHttpClientHeaders(HttpClient client)
    {
        try { client.DefaultRequestHeaders.Remove("User-Agent"); } catch { /* ignore */ }
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", AcceptJson);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        // Client hints — Chromium browsers send these; missing set is a bot signal.
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", SecChUaMobile);
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", SecChUaPlatform);
    }

    /// <summary>
    /// Apply a browser-like header set to a single request (JSON XHR style by default).
    /// </summary>
    public static void ApplyBrowserHeaders(
        HttpRequestMessage request,
        Uri? pageUrl = null,
        bool acceptJson = true,
        string fetchSite = "same-origin",
        string fetchMode = "cors",
        string fetchDest = "empty",
        bool includeClientHints = true)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        request.Headers.TryAddWithoutValidation(
            "Accept", acceptJson ? AcceptJson : AcceptHtml);

        if (includeClientHints)
        {
            request.Headers.TryAddWithoutValidation("sec-ch-ua", SecChUa);
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", SecChUaMobile);
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", SecChUaPlatform);
        }

        // Sec-Fetch-* mirrors real Chrome XHR / navigation
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", fetchMode);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", fetchDest);

        if (pageUrl is not null)
        {
            var origin = pageUrl.GetLeftPart(UriPartial.Authority);
            var referer = origin.TrimEnd('/') + "/";
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }
    }

    /// <summary>Seed fallback UA before WebView2 is ready.</summary>
    public static void PrimeUserAgent()
    {
        _userAgent ??= FallbackChromeUserAgent;
        if (_secChUa is null)
            DeriveClientHintsFromUserAgent(_userAgent);
    }

    /// <summary>
    /// Pin UA to the live WebView2 navigator.userAgent so HttpClient and CF challenge
    /// share the same string (required for cf_clearance).
    /// </summary>
    public static async Task PrimeUserAgentFromWebViewAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        if (core is null) return;
        try
        {
            // Once pinned, only re-apply the same string — never re-sample after CF clearance
            // was issued (changing UA invalidates cf_clearance binding).
            if (_primedFromWebView && !string.IsNullOrEmpty(_userAgent))
            {
                core.Settings.UserAgent = _userAgent;
                return;
            }

            // Seed WebView with our fallback so navigator.userAgent is stable before first pin.
            core.Settings.UserAgent = UserAgent;
            var raw = await core.ExecuteScriptAsync("navigator.userAgent");
            var ua = UnwrapJsString(raw);
            if (!string.IsNullOrWhiteSpace(ua) && ua.Contains("Mozilla", StringComparison.Ordinal))
            {
                UserAgent = ua;
                core.Settings.UserAgent = UserAgent;
                _primedFromWebView = true;
                AppLog.Network("Pinned CF/API user-agent to WebView2 browser string");
            }

            try
            {
                var brandsRaw = await core.ExecuteScriptAsync(
                    "(() => { try { return JSON.stringify(navigator.userAgentData||null); } catch(e) { return 'null'; } })()");
                var brandsJson = UnwrapJsString(brandsRaw);
                if (!string.IsNullOrWhiteSpace(brandsJson) && brandsJson != "null")
                    TryApplyUserAgentData(brandsJson);
            }
            catch { /* older WebView2 */ }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ua", "prime from WebView2: " + ex.Message);
            PrimeUserAgent();
        }
    }

    public static bool IsPrimedFromWebView => _primedFromWebView;

    /// <summary>
    /// Import cookies from WebView2 cookie manager into the shared jar.
    /// Replaces same-name cookies so a fresh cf_clearance always wins over a stale one.
    /// </summary>
    public static async Task SyncWebViewCookiesToHttpAsync(Uri baseUrl, bool force = false)
    {
        lock (SyncGate)
        {
            if (!force && _lastSyncAt is not null &&
                DateTime.UtcNow - _lastSyncAt < TimeSpan.FromSeconds(2))
                return;
            _lastSyncAt = DateTime.UtcNow;
        }

        try
        {
            var cookies = await WebViewAPIClient.Shared.GetCookiesAsync(baseUrl);
            if (cookies.Count == 0)
            {
                AppLog.Warning("cookies", "WebView returned 0 cookies for " + baseUrl.Host);
                return;
            }

            // Expire prior jar entries for this host so we don't keep a dead cf_clearance.
            try
            {
                foreach (Cookie old in CookieJar.GetCookies(baseUrl))
                {
                    if (string.Equals(old.Name, "cf_clearance", StringComparison.Ordinal) ||
                        old.Name is "_t" or "_forum_session" ||
                        old.Name.StartsWith("cf_", StringComparison.Ordinal))
                    {
                        old.Expired = true;
                    }
                }
            }
            catch { /* ignore */ }

            var names = new List<string>();
            foreach (var c in cookies)
            {
                try
                {
                    var domain = string.IsNullOrWhiteSpace(c.Domain) ? baseUrl.Host : c.Domain!;
                    // CookieContainer wants domain without leading dot for some hosts; keep as-is when set.
                    var path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path!;
                    var cookie = new Cookie(c.Name, c.Value ?? "", path, domain)
                    {
                        Secure = c.IsSecure,
                        HttpOnly = c.IsHttpOnly
                    };
                    // WebView2 CoreWebView2Cookie.Expires is seconds since UNIX epoch (double).
                    if (c.Expires > 0)
                    {
                        try
                        {
                            cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(
                                (long)Math.Floor(c.Expires)).UtcDateTime;
                        }
                        catch
                        {
                            // session cookie
                        }
                    }

                    // Prefer Add(uri) for host-scoped; also try domain form.
                    try { CookieJar.Add(baseUrl, cookie); }
                    catch
                    {
                        try { CookieJar.Add(cookie); } catch { /* skip */ }
                    }
                    names.Add(c.Name);
                }
                catch { /* skip bad cookie */ }
            }

            var hasCf = names.Any(n => n == "cf_clearance");
            var hasSess = names.Any(n => n is "_t" or "_forum_session");
            AppLog.Network(
                $"Cookie sync: {names.Count} cookies (cf_clearance={hasCf}, session={hasSess}) " +
                string.Join(",", names.Take(12)));
        }
        catch (Exception ex)
        {
            AppLog.Warning("cookies", "SyncWebViewCookies: " + ex.Message);
        }
    }

    /// <summary>Discourse login cookies only — NOT cf_clearance.</summary>
    public static bool HasLoginCookie(Uri baseUrl)
    {
        try
        {
            return CookieJar.GetCookies(baseUrl).Cast<Cookie>()
                .Any(c => c.Name is "_t" or "_forum_session");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Any site session useful for reads (login or CF clearance).</summary>
    public static bool HasSessionCookie(Uri baseUrl)
    {
        try
        {
            return HasLoginCookie(baseUrl) || HasCloudflareClearance(baseUrl);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasCloudflareClearance(Uri baseUrl)
    {
        try
        {
            return CookieJar.GetCookies(baseUrl).Cast<Cookie>()
                .Any(c => c.Name == "cf_clearance");
        }
        catch
        {
            return false;
        }
    }

    public static async Task ClearCookiesAsync(Uri baseUrl)
    {
        try
        {
            var cookies = CookieJar.GetCookies(baseUrl).Cast<Cookie>().ToList();
            foreach (var c in cookies)
                c.Expired = true;
        }
        catch
        {
            // ignore
        }
        await WebViewAPIClient.Shared.ClearCookiesAsync(baseUrl);
    }

    public static string BuildCookieHeader(Uri url)
    {
        try
        {
            var cookies = CookieJar.GetCookies(url).Cast<Cookie>();
            return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch
        {
            return "";
        }
    }

    private static void DeriveClientHintsFromUserAgent(string ua)
    {
        // Prefer Edge brand if Edg/ present, else Chrome.
        var isEdge = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase);
        var major = "138";
        var m = ChromeMajorRegex().Match(ua);
        if (m.Success) major = m.Groups[1].Value;
        else
        {
            var me = EdgeMajorRegex().Match(ua);
            if (me.Success) major = me.Groups[1].Value;
        }

        var brand = isEdge ? "Microsoft Edge" : "Google Chrome";
        _secChUa = $"\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"{major}\", \"{brand}\";v=\"{major}\"";
        _secChUaFull =
            $"\"Not)A;Brand\";v=\"10.0.0.0\", \"Chromium\";v=\"{major}.0.0.0\", \"{brand}\";v=\"{major}.0.0.0\"";
        _secChUaPlatform = "\"Windows\"";
    }

    private static void TryApplyUserAgentData(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            if (root.TryGetProperty("brands", out var brands) && brands.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var b in brands.EnumerateArray())
                {
                    var brand = b.TryGetProperty("brand", out var br) ? br.GetString() : null;
                    var ver = b.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(brand) && !string.IsNullOrEmpty(ver))
                        parts.Add($"\"{brand}\";v=\"{ver}\"");
                }
                if (parts.Count > 0)
                    _secChUa = string.Join(", ", parts);
            }

            if (root.TryGetProperty("platform", out var plat))
            {
                var p = plat.GetString();
                if (!string.IsNullOrEmpty(p))
                    _secChUaPlatform = $"\"{p}\"";
            }
        }
        catch
        {
            // ignore parse errors
        }
    }

    private static string UnwrapJsString(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => doc.RootElement.GetString() ?? "",
                _ => doc.RootElement.GetRawText().Trim('"')
            };
        }
        catch
        {
            return raw.Trim().Trim('"');
        }
    }

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeMajorRegex();

    [GeneratedRegex(@"Edg/(\d+)")]
    private static partial Regex EdgeMajorRegex();
}
