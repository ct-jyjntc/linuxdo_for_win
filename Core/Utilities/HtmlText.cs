using System.Net;
using System.Text.RegularExpressions;

namespace LinuxDo.Core.Utilities;

public static partial class HtmlText
{
    private static readonly Dictionary<string, string> PlainCache = new();
    private static readonly object Gate = new();

    public static void ClearCache()
    {
        lock (Gate) PlainCache.Clear();
    }

    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        lock (Gate)
        {
            if (PlainCache.TryGetValue(html, out var cached)) return cached;
        }

        var text = NormalizeStructural(html);
        text = EmojiImageRegex().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : "");
        text = LiRegex().Replace(text, "\n• ");
        text = DecodeEntities(StripTags(text));
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        text = text.Trim();

        lock (Gate)
        {
            if (PlainCache.Count > 400) PlainCache.Clear();
            PlainCache[html] = text;
        }
        return text;
    }

    private static string NormalizeStructural(string html)
    {
        var text = BrRegex().Replace(html, "\n");
        text = BlockRegex().Replace(text, "\n\n");
        return text;
    }

    private static string StripTags(string html)
        => TagRegex().Replace(html, "");

    private static string DecodeEntities(string text)
        => WebUtility.HtmlDecode(text) ?? text;

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"</?(p|div|h[1-6]|blockquote|pre|ul|ol|table|tr)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LiRegex();

    [GeneratedRegex(@"<img[^>]*class=[""'][^""']*emoji[^""']*[""'][^>]*alt=[""']([^""']*)[""'][^>]*/?>|<img[^>]*alt=[""']([^""']*)[""'][^>]*class=[""'][^""']*emoji[^""']*[""'][^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmojiImageRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();
}
