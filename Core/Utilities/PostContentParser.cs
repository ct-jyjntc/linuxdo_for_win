using System.Net;
using System.Text.RegularExpressions;

namespace LinuxDo.Core.Utilities;

/// <summary>Parses Discourse cooked HTML into displayable blocks.</summary>
public static partial class PostContentParser
{
    public abstract record Block
    {
        public sealed record Text(string Content, string? Html = null) : Block;
        public sealed record Heading(int Level, string Content) : Block;
        public sealed record ListItem(bool Ordered, int? Index, string Content) : Block;
        public sealed record Image(Uri Url, string? Alt, int? Width, int? Height) : Block;
        public sealed record LinkCard(string Title, Uri Url, string? Host, string? Excerpt) : Block;
        public sealed record Quote(string? Author, string? Title, Uri? TopicUrl, string Content) : Block;
        public sealed record Code(string Content) : Block;
        public sealed record Spoiler(string Content) : Block;
        public sealed record Table(IReadOnlyList<IReadOnlyList<string>> Rows) : Block;
        public sealed record Video(Uri Url) : Block;
        public sealed record HorizontalRule : Block;
    }

    public sealed record IdentifiedBlock(string Id, Block Content);

    public static List<IdentifiedBlock> ParseIdentified(string? html, Uri baseUrl)
    {
        var blocks = Parse(html, baseUrl);
        return blocks.Select((b, i) => new IdentifiedBlock($"{i}-{b.GetType().Name}", b)).ToList();
    }

    public static List<Block> Parse(string? html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var working = Preprocess(html);
        var blocks = new List<Block>();

        working = Extract(working, AsideQuoteRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            blocks.Add(ParseDiscourseQuote(chunk, baseUrl)
                       ?? new Block.Quote(null, null, null, HtmlText.PlainText(chunk)));
        });

        working = Extract(working, OneboxRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            if (ParseOnebox(chunk, baseUrl) is { } card) blocks.Add(card);
            else ProcessInline(chunk, baseUrl, blocks);
        });

        working = Extract(working, SpoilerRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var body = HtmlText.PlainText(chunk).Trim();
            if (!string.IsNullOrEmpty(body)) blocks.Add(new Block.Spoiler(body));
        });

        working = Extract(working, TableRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var rows = ParseTable(chunk);
            if (rows.Count > 0) blocks.Add(new Block.Table(rows));
        });

        working = Extract(working, VideoRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var src = Attr(chunk, "src") ?? Attr(chunk, "href");
            if (src is not null && AbsoluteUrl(src, baseUrl) is { } url)
                blocks.Add(new Block.Video(url));
        });

        working = Extract(working, BlockquoteRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            blocks.Add(new Block.Quote(null, null, null, StripOuter(chunk, "blockquote")));
        });

        working = Extract(working, HeadingRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var level = 3;
            var m = HeadingLevelRegex().Match(chunk);
            if (m.Success) level = int.Parse(m.Groups[1].Value);
            var inner = HeadingInnerRegex().Match(chunk) is { Success: true } im
                ? im.Groups[1].Value : chunk;
            var plain = HtmlText.PlainText(inner).Trim();
            if (!string.IsNullOrEmpty(plain))
                blocks.Add(new Block.Heading(Math.Clamp(level, 1, 6), plain));
        });

        working = Extract(working, ListRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var ordered = chunk.Contains("<ol", StringComparison.OrdinalIgnoreCase);
            var items = ExtractListItems(chunk);
            for (var i = 0; i < items.Count; i++)
                blocks.Add(new Block.ListItem(ordered, ordered ? i + 1 : null, items[i]));
        });

        working = Extract(working, HrRegex(), (_, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            blocks.Add(new Block.HorizontalRule());
        });

        working = Extract(working, PreRegex(), (chunk, before) =>
        {
            ProcessInline(before, baseUrl, blocks);
            var code = HtmlText.PlainText(chunk);
            if (!string.IsNullOrEmpty(code)) blocks.Add(new Block.Code(code));
        });

        ProcessInline(working, baseUrl, blocks);
        return MergeAdjacentText(blocks);
    }

    /// <summary>
    /// Walk matches in order, invoke handler with (match, textBefore), return remaining tail.
    /// </summary>
    private static string Extract(string input, Regex regex, Action<string, string> onMatch)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var matches = regex.Matches(input);
        if (matches.Count == 0) return input;

        var cursor = 0;
        foreach (Match m in matches)
        {
            var before = input[cursor..m.Index];
            onMatch(m.Value, before);
            cursor = m.Index + m.Length;
        }
        return cursor < input.Length ? input[cursor..] : "";
    }

    private static string Preprocess(string html)
    {
        var s = html;
        s = NoiseMetaRegex().Replace(s, "");
        s = NoiseFilenameRegex().Replace(s, "");
        s = NoiseInfoRegex().Replace(s, "");
        s = NoiseExpandRegex().Replace(s, "");
        s = SvgRegex().Replace(s, "");
        return s;
    }

    private static void ProcessInline(string html, Uri baseUrl, List<Block> blocks)
    {
        if (string.IsNullOrWhiteSpace(html)) return;
        var matches = ImgOrLightboxRegex().Matches(html);
        var cursor = 0;
        foreach (Match m in matches)
        {
            if (cursor < m.Index)
                AppendText(html[cursor..m.Index], blocks);
            if (ParseImage(m.Value, baseUrl) is { } img)
                blocks.Add(img);
            cursor = m.Index + m.Length;
        }
        if (cursor < html.Length)
            AppendText(html[cursor..], blocks);
    }

    private static void AppendText(string html, List<Block> blocks)
    {
        var plain = HtmlText.PlainText(html).Trim();
        if (string.IsNullOrEmpty(plain)) return;
        blocks.Add(new Block.Text(plain, html));
    }

    private static Block.Image? ParseImage(string html, Uri baseUrl)
    {
        var href = Attr(html, "href");
        var src = Attr(html, "src") ?? Attr(html, "data-src") ?? Attr(html, "data-orig-src");
        var candidate = PreferredImage(href, src);
        if (candidate is null || AbsoluteUrl(candidate, baseUrl) is not { } url) return null;
        var lower = url.AbsoluteUri.ToLowerInvariant();
        if (lower.Contains("/images/emoji") || lower.Contains("emoji?") || lower.Contains("/emoji/"))
            return null;
        var alt = Attr(html, "alt");
        int? w = int.TryParse(Attr(html, "width"), out var ww) ? ww : null;
        int? h = int.TryParse(Attr(html, "height"), out var hh) ? hh : null;
        return new Block.Image(url, alt, w, h);
    }

    private static string? PreferredImage(string? href, string? src)
    {
        if (href is not null &&
            (href.Contains("uploads", StringComparison.OrdinalIgnoreCase) ||
             href.Contains("/original/", StringComparison.OrdinalIgnoreCase) ||
             href.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             href.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
             href.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
             href.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
             href.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
            return href;
        return href ?? src;
    }

    private static Block.Quote? ParseDiscourseQuote(string chunk, Uri baseUrl)
    {
        var author = Attr(chunk, "data-username");
        string? title = null;
        var titleMatch = QuoteTitleRegex().Match(chunk);
        if (titleMatch.Success)
        {
            var t = HtmlText.PlainText(titleMatch.Groups[1].Value).Trim();
            if (!string.IsNullOrEmpty(t)) title = t;
        }
        Uri? topicUrl = null;
        var href = Attr(chunk, "href");
        if (href is not null) topicUrl = AbsoluteUrl(href, baseUrl);
        var bodyMatch = BlockquoteInnerRegex().Match(chunk);
        var body = bodyMatch.Success ? bodyMatch.Groups[1].Value : chunk;
        var plain = HtmlText.PlainText(body).Trim();
        if (string.IsNullOrEmpty(plain) && string.IsNullOrEmpty(author)) return null;
        return new Block.Quote(author, title, topicUrl, plain);
    }

    private static Block.LinkCard? ParseOnebox(string chunk, Uri baseUrl)
    {
        var href = Attr(chunk, "href");
        if (href is null || AbsoluteUrl(href, baseUrl) is not { } url) return null;
        var title = HtmlText.PlainText(chunk).Split('\n').FirstOrDefault()?.Trim() ?? url.Host;
        return new Block.LinkCard(title, url, url.Host, null);
    }

    private static List<List<string>> ParseTable(string html)
    {
        var rows = new List<List<string>>();
        foreach (Match row in TrRegex().Matches(html))
        {
            var cells = new List<string>();
            foreach (Match cell in TdRegex().Matches(row.Value))
                cells.Add(HtmlText.PlainText(cell.Groups[1].Value).Trim());
            if (cells.Count > 0) rows.Add(cells);
        }
        return rows;
    }

    private static List<string> ExtractListItems(string listHtml)
    {
        var items = new List<string>();
        foreach (Match m in LiRegex().Matches(listHtml))
        {
            var plain = HtmlText.PlainText(m.Groups[1].Value).Trim();
            if (!string.IsNullOrEmpty(plain)) items.Add(plain);
        }
        return items;
    }

    private static List<Block> MergeAdjacentText(List<Block> blocks)
    {
        var result = new List<Block>();
        foreach (var b in blocks)
        {
            if (b is Block.Text t && result.Count > 0 && result[^1] is Block.Text prev)
                result[^1] = new Block.Text(prev.Content + "\n\n" + t.Content);
            else
                result.Add(b);
        }
        return result;
    }

    private static string? Attr(string html, string name)
    {
        var m = Regex.Match(html, $@"\b{Regex.Escape(name)}=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static Uri? AbsoluteUrl(string value, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal))
            value = "https:" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs)) return abs;
        try { return new Uri(baseUrl, value); }
        catch { return null; }
    }

    private static string StripOuter(string html, string tag)
    {
        var m = Regex.Match(html, $@"<{tag}\b[^>]*>([\s\S]*?)</{tag}>", RegexOptions.IgnoreCase);
        return m.Success ? HtmlText.PlainText(m.Groups[1].Value).Trim() : HtmlText.PlainText(html).Trim();
    }

    [GeneratedRegex(@"<aside\b[^>]*class=[""'][^""']*\bquote\b[^""']*[""'][^>]*>[\s\S]*?</aside>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AsideQuoteRegex();
    [GeneratedRegex(@"<aside\b[^>]*class=[""'][^""']*\bonebox\b[^""']*[""'][^>]*>[\s\S]*?</aside>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OneboxRegex();
    [GeneratedRegex(@"<details[\s\S]*?</details>|<div[^>]*class=[""'][^""']*spoiler[^""']*[""'][^>]*>[\s\S]*?</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SpoilerRegex();
    [GeneratedRegex(@"<table[\s\S]*?</table>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TableRegex();
    [GeneratedRegex(@"<video[\s\S]*?</video>|<iframe\b[^>]*>[\s\S]*?</iframe>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VideoRegex();
    [GeneratedRegex(@"<blockquote[\s\S]*?</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockquoteRegex();
    [GeneratedRegex(@"<h[1-6]\b[^>]*>[\s\S]*?</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"<h([1-6])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HeadingLevelRegex();
    [GeneratedRegex(@"<h[1-6]\b[^>]*>([\s\S]*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HeadingInnerRegex();
    [GeneratedRegex(@"<ul\b[^>]*>[\s\S]*?</ul>|<ol\b[^>]*>[\s\S]*?</ol>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ListRegex();
    [GeneratedRegex(@"<hr\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HrRegex();
    [GeneratedRegex(@"<pre[\s\S]*?</pre>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PreRegex();
    [GeneratedRegex(@"(<a[^>]*class=[""'][^""']*lightbox[^""']*[""'][^>]*>[\s\S]*?</a>)|(<div[^>]*class=[""'][^""']*lightbox-wrapper[^""']*[""'][^>]*>[\s\S]*?</div>)|(<img\b[^>]*>)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ImgOrLightboxRegex();
    [GeneratedRegex(@"<div[^>]*class=[""'][^""']*title[^""']*[""'][^>]*>([\s\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QuoteTitleRegex();
    [GeneratedRegex(@"<blockquote\b[^>]*>([\s\S]*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockquoteInnerRegex();
    [GeneratedRegex(@"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrRegex();
    [GeneratedRegex(@"<t[hd][^>]*>([\s\S]*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TdRegex();
    [GeneratedRegex(@"<li\b[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LiRegex();
    [GeneratedRegex(@"<span[^>]*class=[""'][^""']*meta[^""']*[""'][^>]*>[\s\S]*?</span>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NoiseMetaRegex();
    [GeneratedRegex(@"<span[^>]*class=[""'][^""']*filename[^""']*[""'][^>]*>[\s\S]*?</span>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NoiseFilenameRegex();
    [GeneratedRegex(@"<span[^>]*class=[""'][^""']*informations[^""']*[""'][^>]*>[\s\S]*?</span>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NoiseInfoRegex();
    [GeneratedRegex(@"<span[^>]*class=[""'][^""']*expand[^""']*[""'][^>]*>[\s\S]*?</span>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NoiseExpandRegex();
    [GeneratedRegex(@"<svg[\s\S]*?</svg>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SvgRegex();
}
