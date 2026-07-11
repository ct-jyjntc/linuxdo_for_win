using System.Net;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>
/// Shared cookie jar between WebView2 and HttpClient so web login works for API calls.
/// </summary>
public static class CookieSessionBridge
{
    public static readonly CookieContainer CookieJar = new();
    private static readonly object SyncGate = new();
    private static DateTime? _lastSyncAt;
    private static string? _userAgent;

    public static string UserAgent
    {
        get
        {
            if (!string.IsNullOrEmpty(_userAgent)) return _userAgent!;
            return FallbackChromeUserAgent;
        }
        set => _userAgent = value;
    }

    public static string FallbackChromeUserAgent =>
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

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
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    public static void PrimeUserAgent()
    {
        _userAgent ??= FallbackChromeUserAgent;
    }

    /// <summary>Import cookies from WebView2 cookie manager into the shared jar.</summary>
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
            foreach (var c in cookies)
            {
                try
                {
                    var cookie = new Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain ?? baseUrl.Host)
                    {
                        Secure = c.IsSecure,
                        HttpOnly = c.IsHttpOnly
                    };
                    // CoreWebView2Cookie.Expires is OLE Automation date (double). 0 = session cookie.
                    if (c.Expires > 0)
                    {
                        try { cookie.Expires = DateTime.FromOADate(c.Expires); }
                        catch { /* ignore invalid OA dates */ }
                    }
                    CookieJar.Add(baseUrl, cookie);
                }
                catch (Exception ex)
                {
                    AppLog.Warning("cookie", $"Skip cookie {c.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("cookie", $"Sync failed: {ex.Message}");
        }
    }

    public static bool HasSessionCookie(Uri baseUrl)
    {
        try
        {
            var cookies = CookieJar.GetCookies(baseUrl).Cast<Cookie>();
            return cookies.Any(c =>
                c.Name is "_t" or "_forum_session" or "cf_clearance" ||
                c.Name.Contains("session", StringComparison.OrdinalIgnoreCase));
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
        lock (SyncGate) _lastSyncAt = null;
    }

    public static string BuildCookieHeader(Uri url)
    {
        try
        {
            var cookies = CookieJar.GetCookies(url).Cast<Cookie>()
                .Where(c => !c.Expired)
                .Select(c => $"{c.Name}={c.Value}");
            return string.Join("; ", cookies);
        }
        catch
        {
            return "";
        }
    }
}
