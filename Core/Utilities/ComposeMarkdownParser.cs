using System.Text.RegularExpressions;

namespace LinuxDo.Core.Utilities;

/// <summary>Lightweight Markdown parser for compose preview (headings, lists, code, images, links).</summary>
public static partial class ComposeMarkdownParser
{
    public abstract record Block
    {
        public sealed record Paragraph(string Text) : Block;
        public sealed record Heading(int Level, string Text) : Block;
        public sealed record Quote(string Text) : Block;
        public sealed record Code(string Text) : Block;
        public sealed record ListItem(string Text) : Block;
        public sealed record Image(string Alt, string Url, int? Width, int? Height) : Block;
        public sealed record LinkCard(Uri Url) : Block;
        public sealed record HorizontalRule : Block;
    }

    public static List<Block> Parse(string? markdown, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                i++;
                continue;
            }

            // Fenced code
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var code = new System.Text.StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing fence
                blocks.Add(new Block.Code(code.ToString()));
                continue;
            }

            // HR
            if (HrRegex().IsMatch(trimmed))
            {
                blocks.Add(new Block.HorizontalRule());
                i++;
                continue;
            }

            // Heading
            var hm = HeadingRegex().Match(trimmed);
            if (hm.Success)
            {
                blocks.Add(new Block.Heading(hm.Groups[1].Value.Length, InlinePlain(hm.Groups[2].Value)));
                i++;
                continue;
            }

            // Quote (collect consecutive)
            if (trimmed.StartsWith('>'))
            {
                var q = new System.Text.StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var body = lines[i].TrimStart()[1..].TrimStart();
                    if (q.Length > 0) q.Append('\n');
                    q.Append(body);
                    i++;
                }
                blocks.Add(new Block.Quote(InlinePlain(q.ToString())));
                continue;
            }

            // List item
            if (ListRegex().IsMatch(trimmed))
            {
                var m = ListRegex().Match(trimmed);
                blocks.Add(new Block.ListItem(InlinePlain(m.Groups[1].Value)));
                i++;
                continue;
            }

            // Image-only line
            var im = ImageOnlyRegex().Match(trimmed);
            if (im.Success)
            {
                var (alt, w, h) = ParseAlt(im.Groups[1].Value);
                var rawUrl = im.Groups[2].Value.Trim();
                blocks.Add(new Block.Image(alt, rawUrl, w, h));
                // bare URL on its own may also become link card later if not image
                i++;
                continue;
            }

            // Bare URL → link card
            if (BareUrlRegex().IsMatch(trimmed) && !trimmed.Contains(' '))
            {
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    blocks.Add(new Block.LinkCard(u));
                    i++;
                    continue;
                }
            }

            // Paragraph: collect until blank
            var para = new System.Text.StringBuilder(line);
            i++;
            while (i < lines.Length)
            {
                var next = lines[i];
                if (string.IsNullOrWhiteSpace(next)) break;
                var nt = next.Trim();
                if (nt.StartsWith("```", StringComparison.Ordinal) ||
                    nt.StartsWith('#') ||
                    nt.StartsWith('>') ||
                    ListRegex().IsMatch(nt) ||
                    HrRegex().IsMatch(nt) ||
                    ImageOnlyRegex().IsMatch(nt))
                    break;
                para.Append('\n').Append(next);
                i++;
            }

            var text = para.ToString();
            // Extract markdown images inside paragraph as separate blocks when whole-line-ish
            text = ImageInlineRegex().Replace(text, m =>
            {
                var (alt, w, h) = ParseAlt(m.Groups[1].Value);
                blocks.Add(new Block.Image(alt, m.Groups[2].Value.Trim(), w, h));
                return "";
            });
            text = InlinePlain(text).Trim();
            if (!string.IsNullOrEmpty(text))
                blocks.Add(new Block.Paragraph(text));
        }

        return blocks;
    }

    public static string InlinePlain(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // links [text](url) → text
        text = LinkRegex().Replace(text, m => m.Groups[1].Value);
        // bold/italic
        text = BoldRegex().Replace(text, m => m.Groups[1].Value);
        text = ItalicRegex().Replace(text, m => m.Groups[1].Value);
        text = CodeInlineRegex().Replace(text, m => m.Groups[1].Value);
        return text;
    }

    public static string ToPreviewPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var blocks = Parse(markdown, new Uri("https://linux.do"));
        var sb = new System.Text.StringBuilder();
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Block.Heading h:
                    sb.AppendLine(new string('#', h.Level) + " " + h.Text);
                    sb.AppendLine();
                    break;
                case Block.Paragraph p:
                    sb.AppendLine(p.Text);
                    sb.AppendLine();
                    break;
                case Block.Quote q:
                    foreach (var line in q.Text.Split('\n'))
                        sb.AppendLine("> " + line);
                    sb.AppendLine();
                    break;
                case Block.Code c:
                    sb.AppendLine("```");
                    sb.AppendLine(c.Text);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    break;
                case Block.ListItem li:
                    sb.AppendLine("• " + li.Text);
                    break;
                case Block.Image img:
                    sb.AppendLine($"🖼 {img.Alt} ({img.Url})");
                    break;
                case Block.LinkCard card:
                    sb.AppendLine($"🔗 {card.Url}");
                    break;
                case Block.HorizontalRule:
                    sb.AppendLine("———");
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static (string Alt, int? Width, int? Height) ParseAlt(string altRaw)
    {
        // Discourse style: name|640x480
        var parts = altRaw.Split('|');
        var alt = parts[0].Trim();
        int? w = null, h = null;
        if (parts.Length > 1)
        {
            var dim = parts[1].Trim();
            var m = Regex.Match(dim, @"(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                w = int.Parse(m.Groups[1].Value);
                h = int.Parse(m.Groups[2].Value);
            }
        }
        return (string.IsNullOrEmpty(alt) ? "image" : alt, w, h);
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\*|-|_)\s*\1\s*\1[\s\1]*$")]
    private static partial Regex HrRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d+\.)\s+(.+)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"^!\[([^\]]*)\]\(([^)]+)\)$")]
    private static partial Regex ImageOnlyRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex ImageInlineRegex();

    [GeneratedRegex(@"^https?://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|_(.+?)_")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeInlineRegex();
}
