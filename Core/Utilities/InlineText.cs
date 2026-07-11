using System.Net;
using System.Text.RegularExpressions;

namespace LinuxDo.Core.Utilities;

public sealed record InlineSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    string? LinkUrl = null,
    bool IsMention = false,
    string? MentionUser = null);

/// <summary>Parse simple HTML inline markup into display spans (bold / italic / code / links / @mentions).</summary>
public static partial class InlineText
{
    public static IReadOnlyList<InlineSpan> FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var working = html;
        working = EmojiImgRegex().Replace(working, m => m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Value ?? ""));
        working = BrRegex().Replace(working, "\n");
        working = BlockOpenCloseRegex().Replace(working, "");

        var spans = new List<InlineSpan>();
        ParseRecursive(working, spans, bold: false, italic: false, code: false, link: null, mention: false, mentionUser: null);
        return Merge(spans);
    }

    public static IReadOnlyList<InlineSpan> FromPlain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var spans = new List<InlineSpan>();
        AppendWithMentions(text, spans, bold: false, italic: false, code: false, link: null);
        return Merge(spans);
    }

    private static void ParseRecursive(
        string html, List<InlineSpan> spans,
        bool bold, bool italic, bool code, string? link,
        bool mention, string? mentionUser)
    {
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AppendDecoded(html[i..], spans, bold, italic, code, link, mention, mentionUser);
                break;
            }
            if (lt > i)
                AppendDecoded(html[i..lt], spans, bold, italic, code, link, mention, mentionUser);

            var gt = html.IndexOf('>', lt);
            if (gt < 0)
            {
                AppendDecoded(html[lt..], spans, bold, italic, code, link, mention, mentionUser);
                break;
            }

            var tag = html[(lt + 1)..gt];
            i = gt + 1;
            var slash = tag.StartsWith('/');
            var namePart = slash ? tag[1..] : tag;
            var space = namePart.IndexOfAny([' ', '\t', '\r', '\n']);
            var name = (space > 0 ? namePart[..space] : namePart).Trim().ToLowerInvariant();

            if (slash) continue;

            if (name is "br")
            {
                spans.Add(new InlineSpan("\n", bold, italic, code, link, mention, mentionUser));
                continue;
            }

            if (name is "img")
            {
                var alt = Attr(tag, "alt");
                if (!string.IsNullOrEmpty(alt))
                    spans.Add(new InlineSpan(alt!, bold, italic, code, link, mention, mentionUser));
                continue;
            }

            if (name is "strong" or "b" or "em" or "i" or "code" or "a" or "span")
            {
                var close = FindClose(html, i, name);
                if (close < 0) continue;
                var inner = html[i..close];
                var nextBold = bold || name is "strong" or "b";
                var nextItalic = italic || name is "em" or "i";
                var nextCode = code || name is "code";
                var nextLink = link;
                var nextMention = mention;
                var nextMentionUser = mentionUser;

                if (name == "a")
                {
                    var href = Attr(tag, "href");
                    if (!string.IsNullOrEmpty(href)) nextLink = href;
                    var cls = Attr(tag, "class") ?? "";
                    if (cls.Contains("mention", StringComparison.OrdinalIgnoreCase) ||
                        (href?.Contains("/u/", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        nextMention = true;
                        nextMentionUser = ExtractUsername(href, inner);
                    }
                }
                else if (name == "span")
                {
                    var cls = Attr(tag, "class") ?? "";
                    if (cls.Contains("mention", StringComparison.OrdinalIgnoreCase))
                    {
                        nextMention = true;
                        nextMentionUser = ExtractUsername(null, inner);
                    }
                }

                ParseRecursive(inner, spans, nextBold, nextItalic, nextCode, nextLink, nextMention, nextMentionUser);
                var after = html.IndexOf('>', close);
                i = after >= 0 ? after + 1 : close;
            }
        }
    }

    private static string? ExtractUsername(string? href, string innerHtml)
    {
        if (!string.IsNullOrEmpty(href))
        {
            // /u/username or /u/username/...
            var m = Regex.Match(href, @"/u/([^/?#\s]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        var plain = WebUtility.HtmlDecode(TagRegex().Replace(innerHtml, "")).Trim().TrimStart('@');
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    private static int FindClose(string html, int start, string name)
    {
        var openPat = $"<{name}";
        var closePat = $"</{name}";
        var depth = 1;
        var i = start;
        while (i < html.Length)
        {
            var nextOpen = html.IndexOf(openPat, i, StringComparison.OrdinalIgnoreCase);
            var nextClose = html.IndexOf(closePat, i, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return -1;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var c = nextOpen + openPat.Length;
                if (c < html.Length && (html[c] is '>' or ' ' or '/' or '\t'))
                    depth++;
                i = nextOpen + openPat.Length;
                continue;
            }
            depth--;
            if (depth == 0) return nextClose;
            i = nextClose + closePat.Length;
        }
        return -1;
    }

    private static void AppendDecoded(
        string raw, List<InlineSpan> spans,
        bool bold, bool italic, bool code, string? link,
        bool mention, string? mentionUser)
    {
        if (string.IsNullOrEmpty(raw)) return;
        var text = WebUtility.HtmlDecode(raw);
        if (string.IsNullOrEmpty(text)) return;

        if (mention)
        {
            spans.Add(new InlineSpan(text, bold, italic, code, link, IsMention: true, MentionUser: mentionUser));
            return;
        }

        if (code || !string.IsNullOrEmpty(link))
        {
            spans.Add(new InlineSpan(text, bold, italic, code, link));
            return;
        }

        AppendWithMentions(text, spans, bold, italic, code, link);
    }

    private static void AppendWithMentions(
        string text, List<InlineSpan> spans,
        bool bold, bool italic, bool code, string? link)
    {
        var matches = MentionRegex().Matches(text);
        if (matches.Count == 0)
        {
            spans.Add(new InlineSpan(text, bold, italic, code, link));
            return;
        }

        var cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                spans.Add(new InlineSpan(text[cursor..m.Index], bold, italic, code, link));
            var user = m.Groups[1].Value;
            spans.Add(new InlineSpan(
                m.Value,
                bold,
                italic,
                code,
                LinkUrl: $"/u/{user}",
                IsMention: true,
                MentionUser: user));
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
            spans.Add(new InlineSpan(text[cursor..], bold, italic, code, link));
    }

    private static List<InlineSpan> Merge(List<InlineSpan> spans)
    {
        if (spans.Count == 0) return spans;
        var result = new List<InlineSpan>();
        var cur = spans[0];
        for (var i = 1; i < spans.Count; i++)
        {
            var n = spans[i];
            if (cur.Bold == n.Bold && cur.Italic == n.Italic && cur.Code == n.Code &&
                cur.LinkUrl == n.LinkUrl && cur.IsMention == n.IsMention &&
                cur.MentionUser == n.MentionUser)
            {
                cur = cur with { Text = cur.Text + n.Text };
            }
            else
            {
                result.Add(cur);
                cur = n;
            }
        }
        result.Add(cur);
        return result;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"{name}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"</?(?:p|div)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockOpenCloseRegex();

    [GeneratedRegex(@"<img[^>]*class=[""'][^""']*emoji[^""']*[""'][^>]*alt=[""']([^""']*)[""'][^>]*/?>|<img[^>]*alt=[""']([^""']*)[""'][^>]*class=[""'][^""']*emoji[^""']*[""'][^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmojiImgRegex();

    [GeneratedRegex(@"@([A-Za-z0-9_\-\.]{2,})")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();
}
