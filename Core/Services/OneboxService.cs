using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>Fetches Discourse /onebox or Open Graph metadata for link cards.</summary>
public sealed partial class OneboxService
{
    public static OneboxService Shared { get; } = new();

    public sealed record LinkPreview(
        Uri Url,
        string Title,
        string? Description,
        string? SiteName,
        Uri? ImageUrl);

    private readonly ConcurrentDictionary<string, LinkPreview> _cache = new();
    private readonly ConcurrentDictionary<string, Task<LinkPreview?>> _inflight = new();
    private readonly HttpClient _forumHttp;
    private readonly HttpClient _ogHttp;

    private OneboxService()
    {
        _forumHttp = CookieSessionBridge.CreateHttpClient();
        _forumHttp.Timeout = TimeSpan.FromSeconds(12);
        try { _forumHttp.DefaultRequestHeaders.Remove("Accept"); } catch { /* ignore */ }
        _forumHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", CookieSessionBridge.AcceptHtml);

        _ogHttp = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        CookieSessionBridge.ApplyDefaultHttpClientHeaders(_ogHttp);
        try { _ogHttp.DefaultRequestHeaders.Remove("Accept"); } catch { /* ignore */ }
        _ogHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", CookieSessionBridge.AcceptHtml);
    }

    public LinkPreview? Cached(Uri url)
        => _cache.TryGetValue(url.AbsoluteUri, out var p) ? p : null;

    public async Task<LinkPreview?> PreviewAsync(Uri url)
    {
        var key = url.AbsoluteUri;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        if (_inflight.TryGetValue(key, out var existing)) return await existing;

        var task = FetchAsync(url);
        _inflight[key] = task;
        try
        {
            var result = await task;
            if (result is not null)
            {
                if (_cache.Count > 200)
                {
                    foreach (var k in _cache.Keys.Take(40).ToList())
                        _cache.TryRemove(k, out _);
                }
                _cache[key] = result;
            }
            return result;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void ClearCache() => _cache.Clear();

    public int CacheCount => _cache.Count;

    private async Task<LinkPreview?> FetchAsync(Uri url)
    {
        var discourse = await FetchDiscourseOneboxAsync(url);
        if (discourse is not null) return discourse;
        var og = await FetchOpenGraphAsync(url);
        if (og is not null) return og;
        return new LinkPreview(url, url.Host ?? url.AbsoluteUri, null, url.Host, null);
    }

    private async Task<LinkPreview?> FetchDiscourseOneboxAsync(Uri url)
    {
        try
        {
            var baseUrl = DiscourseAPI.Shared.CurrentBaseUrl;
            var endpoint = new Uri($"{baseUrl.AbsoluteUri.TrimEnd('/')}/onebox?url={Uri.EscapeDataString(url.AbsoluteUri)}&refresh=false");
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            CookieSessionBridge.ApplyBrowserHeaders(
                req,
                pageUrl: baseUrl,
                acceptJson: false,
                fetchSite: "same-origin",
                fetchMode: "cors",
                fetchDest: "empty");
            try { req.Headers.Remove("Accept"); } catch { /* ignore */ }
            req.Headers.TryAddWithoutValidation("Accept", CookieSessionBridge.AcceptHtml);
            using var resp = await _forumHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(html) ||
                html.Contains("just a moment", StringComparison.OrdinalIgnoreCase))
                return null;
            return ParseOneboxHtml(html, url);
        }
        catch
        {
            return null;
        }
    }

    private async Task<LinkPreview?> FetchOpenGraphAsync(Uri url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _ogHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var take = Math.Min(bytes.Length, 200_000);
            var html = System.Text.Encoding.UTF8.GetString(bytes, 0, take);
            return ParseOpenGraph(html, url);
        }
        catch
        {
            return null;
        }
    }

    private static LinkPreview? ParseOneboxHtml(string html, Uri source)
    {
        var title = FirstMatch(html, @"class=[""'][^""']*header[^""']*[""'][\s\S]*?<a[^>]*>([\s\S]*?)</a>")
                    ?? FirstMatch(html, @"<h3[^>]*>\s*<a[^>]*>([\s\S]*?)</a>")
                    ?? FirstMatch(html, @"<a[^>]+class=[""'][^""']*onebox[^""']*[""'][^>]*>([\s\S]*?)</a>");
        var desc = FirstMatch(html, @"<p[^>]*>([\s\S]*?)</p>");
        var site = FirstMatch(html, @"class=[""']domain[""'][^>]*>([\s\S]*?)<")
                   ?? FirstMatch(html, @"class=[""']source[""'][\s\S]*?<a[^>]*>([\s\S]*?)</a>");
        var img = FirstMatch(html, @"<img[^>]+src=[""']([^""']+)[""']");

        var cleanTitle = HtmlText.PlainText(title ?? source.Host ?? source.AbsoluteUri).Trim();
        if (string.IsNullOrEmpty(cleanTitle)) return null;

        return new LinkPreview(
            source,
            cleanTitle,
            NullIfEmpty(HtmlText.PlainText(desc ?? "")),
            NullIfEmpty(HtmlText.PlainText(site ?? "")) ?? source.Host,
            AbsoluteUrl(img, source));
    }

    private static LinkPreview? ParseOpenGraph(string html, Uri source)
    {
        var title = MetaProperty(html, "og:title")
                    ?? MetaName(html, "title")
                    ?? FirstMatch(html, @"<title[^>]*>([\s\S]*?)</title>");
        var desc = MetaProperty(html, "og:description") ?? MetaName(html, "description");
        var site = MetaProperty(html, "og:site_name");
        var image = MetaProperty(html, "og:image");

        var cleanTitle = HtmlText.PlainText(title ?? source.Host ?? source.AbsoluteUri).Trim();
        if (string.IsNullOrEmpty(cleanTitle)) return null;

        return new LinkPreview(
            source,
            cleanTitle,
            NullIfEmpty(HtmlText.PlainText(desc ?? "")),
            NullIfEmpty(HtmlText.PlainText(site ?? "")) ?? source.Host,
            AbsoluteUrl(image, source));
    }

    private static string? MetaProperty(string html, string property)
    {
        var esc = Regex.Escape(property);
        return FirstMatch(html, $@"property=[""']{esc}[""']\s+content=[""']([^""']+)[""']")
               ?? FirstMatch(html, $@"content=[""']([^""']+)[""']\s+property=[""']{esc}[""']");
    }

    private static string? MetaName(string html, string name)
    {
        var esc = Regex.Escape(name);
        return FirstMatch(html, $@"name=[""']{esc}[""']\s+content=[""']([^""']+)[""']")
               ?? FirstMatch(html, $@"content=[""']([^""']+)[""']\s+name=[""']{esc}[""']");
    }

    private static string? FirstMatch(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static Uri? AbsoluteUrl(string? raw, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = WebUtility.HtmlDecode(raw.Trim());
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)) return abs;
        try { return new Uri(baseUrl, raw); }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
