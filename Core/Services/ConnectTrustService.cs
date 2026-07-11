using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public sealed class ConnectTrustData
{
    public string Title { get; set; } = "信任等级";
    public string BadgeText { get; set; } = "";
    public string BadgeKind { get; set; } = "warning"; // success / warning / danger
    public string Subtitle { get; set; } = "";
    public List<Ring> Rings { get; set; } = [];
    public List<Bar> Bars { get; set; } = [];
    public List<Quota> Quotas { get; set; } = [];
    public List<Veto> Vetos { get; set; } = [];
    public string FooterHint { get; set; } = "";
    public string StatusText { get; set; } = "";
    public bool IsStatusMet { get; set; }

    public sealed class Ring
    {
        public string Label { get; set; } = "";
        public double Current { get; set; }
        public double Maximum { get; set; }
        public bool IsMet { get; set; }
        public double Progress => Maximum > 0 ? Math.Clamp(Current / Maximum, 0, 1) : 0;
        public string ValueText => Maximum > 0 ? $"{Current:0.##}/{Maximum:0.##}" : $"{Current:0.##}";
    }

    public sealed class Bar
    {
        public string Label { get; set; } = "";
        public string CurrentText { get; set; } = "";
        public double Progress { get; set; }
        public bool IsMet { get; set; }
    }

    public sealed class Quota
    {
        public string Label { get; set; } = "";
        public string ValueText { get; set; } = "";
        public int UsedSlots { get; set; }
        public int TotalSlots { get; set; } = 5;
        public bool IsMet { get; set; }
    }

    public sealed class Veto
    {
        public string Label { get; set; } = "";
        public string Desc { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsMet { get; set; }
    }
}

/// <summary>Fetches & parses https://connect.linux.do TL progress.</summary>
public static partial class ConnectTrustService
{
    public static readonly Uri ConnectUrl = new("https://connect.linux.do/");

    public static async Task<ConnectTrustData> FetchAsync()
    {
        var html = await FetchHtmlAsync();
        var lower = html.ToLowerInvariant();
        if (lower.Contains("just a moment") || lower.Contains("cf-browser-verification") || lower.Contains("cf-mitigated"))
            throw new APIError.CloudflareChallenge();

        var parsed = Parse(html);
        if (parsed is not null) return parsed;

        if (lower.Contains("login") || html.Contains("登录") || lower.Contains("sign in"))
            throw new APIError.ServerMessage("Connect 需要登录。请先在本客户端完成 linux.do 登录后重试。");
        throw new APIError.ServerMessage("未能解析 Connect 信任等级卡片（页面结构可能已变更）");
    }

    private static async Task<string> FetchHtmlAsync()
    {
        await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(new Uri("https://linux.do"), force: true);

        try
        {
            var html = await FetchHtmlViaHttpAsync();
            if (LooksLikeTrustCard(html)) return html;
            AppLog.Network("Connect URLSession HTML missing TL card; trying WebView extract");
        }
        catch (Exception ex)
        {
            AppLog.Warning("network", "Connect URLSession failed: " + ex.Message);
        }

        return await FetchHtmlViaWebViewAsync();
    }

    private static async Task<string> FetchHtmlViaHttpAsync()
    {
        using var handler = CookieSessionBridge.CreateHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        using var request = new HttpRequestMessage(HttpMethod.Get, ConnectUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", CookieSessionBridge.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Referer", "https://linux.do/");
        using var response = await http.SendAsync(request);
        var data = await response.Content.ReadAsByteArrayAsync();
        var status = (int)response.StatusCode;
        if (status is 403 or 503) throw new APIError.CloudflareChallenge();
        if (status is < 200 or >= 400)
            throw new APIError.Http(status, $"Connect 返回 {status}");
        return Encoding.UTF8.GetString(data);
    }

    private static async Task<string> FetchHtmlViaWebViewAsync()
    {
        await WebViewAPIClient.Shared.EnsureInitializedAsync();
        var data = await WebViewAPIClient.Shared.FetchAsync(
            ConnectUrl, "GET",
            new Dictionary<string, string> { ["Accept"] = "text/html" },
            expectJson: false);
        var html = Encoding.UTF8.GetString(data);
        if (string.IsNullOrWhiteSpace(html))
        {
            // Navigate and extract outerHTML
            var core = WebViewAPIClient.Shared.GetWebView()?.CoreWebView2
                       ?? throw new APIError.ServerMessage("WebView 未初始化");
            var tcs = new TaskCompletionSource<bool>();
            void Handler(object s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= Handler;
                tcs.TrySetResult(e.IsSuccess);
            }
            core.NavigationCompleted += Handler;
            WebViewAPIClient.Shared.GetWebView()!.Source = ConnectUrl;
            await Task.WhenAny(tcs.Task, Task.Delay(15000));
            var result = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            html = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? "";
        }
        if (string.IsNullOrWhiteSpace(html))
            throw new APIError.ServerMessage("无法读取 Connect 页面 HTML");
        return html;
    }

    private static bool LooksLikeTrustCard(string html)
        => html.Contains("tl3-ring") || html.Contains("card-title") ||
           html.Contains("tl3-bar") || html.Contains("信任等级");

    public static ConnectTrustData? Parse(string html)
    {
        var source = FirstMatch(html, @"(?s)<div[^>]*class=""[^""]*\bcard\b[^""]*""[^>]*>(.*)") ?? html;
        return ParseCard(source);
    }

    private static ConnectTrustData? ParseCard(string card)
    {
        var title = StripTags(FirstMatch(card, @"(?s)<h2[^>]*class=""[^""]*card-title[^""]*""[^>]*>(.*?)</h2>") ?? "信任等级");
        var badgeText = StripTags(FirstMatch(card, @"(?s)<[^>]*class=""[^""]*\bbadge\b[^""]*""[^>]*>(.*?)</[^>]+>") ?? "");
        var badgeClass = FirstMatch(card, @"class=""([^""]*\bbadge\b[^""]*)""") ?? "";
        var badgeKind = badgeClass.Contains("badge-success") ? "success"
            : badgeClass.Contains("badge-danger") ? "danger" : "warning";
        var subtitle = StripTags(FirstMatch(card, @"(?s)<[^>]*class=""[^""]*card-subtitle[^""]*""[^>]*>(.*?)</[^>]+>") ?? "");

        var rings = ParseRings(card);
        var bars = ParseBars(card);
        var quotas = ParseQuotas(card);
        var vetos = ParseVetos(card);
        var footerHint = StripTags(FirstMatch(card, @"(?s)<[^>]*class=""[^""]*text-hint[^""]*""[^>]*>(.*?)</[^>]+>") ?? "");
        var statusText = StripTags(FirstMatch(card, @"(?s)<[^>]*class=""[^""]*status-(?:met|unmet)[^""]*""[^>]*>(.*?)</[^>]+>") ?? "");
        var isStatusMet = card.Contains("status-met") && !string.IsNullOrEmpty(statusText);

        if (rings.Count == 0 && bars.Count == 0 && quotas.Count == 0 && string.IsNullOrEmpty(title))
            return null;

        return new ConnectTrustData
        {
            Title = string.IsNullOrEmpty(title) ? "信任等级" : title,
            BadgeText = badgeText,
            BadgeKind = badgeKind,
            Subtitle = subtitle,
            Rings = rings,
            Bars = bars,
            Quotas = quotas,
            Vetos = vetos,
            FooterHint = footerHint,
            StatusText = statusText,
            IsStatusMet = isStatusMet
        };
    }

    private static List<ConnectTrustData.Ring> ParseRings(string card)
    {
        var labels = AllMatches(card, @"(?s)class=""[^""]*tl3-ring-label[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var styles = AllMatches(card, @"class=""[^""]*tl3-ring-circle[^""]*""[^>]*style=""([^""]*)""").ToList();
        var metFlags = AllMatches(card, @"class=""([^""]*tl3-ring-circle[^""]*)""")
            .Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("met")).ToList();
        var count = Math.Max(labels.Count, styles.Count);
        var results = new List<ConnectTrustData.Ring>();
        for (var i = 0; i < count; i++)
        {
            var label = i < labels.Count ? labels[i] : $"指标{i + 1}";
            var style = i < styles.Count ? styles[i] : "";
            var val = CssVar(style, "val");
            var max = CssVar(style, "max");
            var isMet = i < metFlags.Count ? metFlags[i] : max > 0 && val >= max;
            results.Add(new ConnectTrustData.Ring { Label = label, Current = val, Maximum = max, IsMet = isMet });
        }
        return results;
    }

    private static List<ConnectTrustData.Bar> ParseBars(string card)
    {
        var labels = AllMatches(card, @"(?s)class=""[^""]*tl3-bar-label[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var nums = AllMatches(card, @"(?s)class=""[^""]*tl3-bar-nums[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var fillStyles = AllMatches(card, @"class=""[^""]*tl3-bar-fill[^""]*""[^>]*style=""([^""]*)""").ToList();
        var fillClasses = AllMatches(card, @"class=""([^""]*tl3-bar-fill[^""]*)""").ToList();
        var results = new List<ConnectTrustData.Bar>();
        for (var i = 0; i < labels.Count; i++)
        {
            var style = i < fillStyles.Count ? fillStyles[i] : "";
            var val = CssVar(style, "val");
            var max = CssVar(style, "max");
            var progress = max > 0 ? Math.Clamp(val / max, 0, 1) : 0;
            var isMet = i < fillClasses.Count
                ? fillClasses[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("met")
                : progress >= 1;
            results.Add(new ConnectTrustData.Bar
            {
                Label = labels[i],
                CurrentText = i < nums.Count ? nums[i] : "",
                Progress = progress,
                IsMet = isMet
            });
        }
        return results;
    }

    private static List<ConnectTrustData.Quota> ParseQuotas(string card)
    {
        var labels = AllMatches(card, @"(?s)class=""[^""]*tl3-quota-label[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var values = AllMatches(card, @"(?s)class=""[^""]*tl3-quota-nums[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var totalUsed = AllMatches(card, @"class=""[^""]*tl3-slot[^""]*used[^""]*""").Count;
        var results = new List<ConnectTrustData.Quota>();
        for (var i = 0; i < labels.Count; i++)
        {
            var used = labels.Count == 1
                ? totalUsed
                : Math.Min(5, totalUsed / Math.Max(labels.Count, 1) + (i < totalUsed % Math.Max(labels.Count, 1) ? 1 : 0));
            results.Add(new ConnectTrustData.Quota
            {
                Label = labels[i],
                ValueText = i < values.Count ? values[i] : "",
                UsedSlots = Math.Min(used, 5),
                TotalSlots = 5,
                IsMet = !card.Contains("tl3-quota-card unmet") || used == 0
            });
        }
        return results;
    }

    private static List<ConnectTrustData.Veto> ParseVetos(string card)
    {
        var labels = AllMatches(card, @"(?s)class=""[^""]*tl3-veto-label[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var descs = AllMatches(card, @"(?s)class=""[^""]*tl3-veto-desc[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var values = AllMatches(card, @"(?s)class=""[^""]*tl3-veto-value[^""]*""[^>]*>(.*?)</[^>]+>").Select(StripTags).ToList();
        var itemClasses = AllMatches(card, @"class=""([^""]*tl3-veto-item[^""]*)""").ToList();
        var results = new List<ConnectTrustData.Veto>();
        var seen = new HashSet<string>();
        var count = Math.Min(labels.Count, Math.Max(values.Count, 1));
        for (var i = 0; i < count; i++)
        {
            var label = labels[i];
            if (!seen.Add(label)) continue;
            results.Add(new ConnectTrustData.Veto
            {
                Label = label,
                Desc = i < descs.Count ? descs[i] : "",
                Value = i < values.Count ? values[i] : "0",
                IsMet = i < itemClasses.Count ? !itemClasses[i].Contains("unmet") : true
            });
        }
        return results;
    }

    private static double CssVar(string style, string name)
    {
        var m = Regex.Match(style, $@"--{Regex.Escape(name)}:\s*([0-9.]+)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static string? FirstMatch(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups.Count >= 2 ? m.Groups[1].Value : m.Value;
    }

    private static List<string> AllMatches(string text, string pattern)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase))
            list.Add(m.Groups.Count >= 2 ? m.Groups[1].Value : m.Value);
        return list;
    }

    private static string StripTags(string html)
    {
        var s = Regex.Replace(html, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s) ?? s;
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
