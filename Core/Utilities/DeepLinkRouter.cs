using LinuxDo.Core.Services;

namespace LinuxDo.Core.Utilities;

/// <summary>Parses linuxdo:// and https://linux.do deep links into app routes.</summary>
public static class DeepLinkRouter
{
    public static AppRoute? RouteFrom(Uri url)
    {
        var scheme = url.Scheme.ToLowerInvariant();

        if (scheme == "linuxdo")
        {
            if (string.Equals(url.Host, "auth", StringComparison.OrdinalIgnoreCase) ||
                url.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                url.Query.Contains("payload=", StringComparison.OrdinalIgnoreCase))
                return null; // auth flow
            return ParsePath(url.Host, url.AbsolutePath);
        }

        if (scheme is "https" or "http")
        {
            var host = url.Host.ToLowerInvariant();
            if (host != "linux.do" && !host.EndsWith(".linux.do", StringComparison.Ordinal))
                return null;
            return ParseWebPath(url.AbsolutePath);
        }

        return null;
    }

    public static AppRoute? RouteFrom(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var token = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text.Trim();
        if (Uri.TryCreate(token, UriKind.Absolute, out var url))
            return RouteFrom(url);
        // bare path-like
        if (token.StartsWith("/t/", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("t/", StringComparison.OrdinalIgnoreCase))
            return ParseWebPath(token.StartsWith('/') ? token : "/" + token);
        return null;
    }

    private static AppRoute? ParsePath(string? host, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!string.IsNullOrEmpty(host) &&
            !string.Equals(host, "auth", StringComparison.OrdinalIgnoreCase))
            parts.Insert(0, host);
        return ParseComponents(parts);
    }

    private static AppRoute? ParseWebPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        return ParseComponents(parts);
    }

    private static AppRoute? ParseComponents(List<string> parts)
    {
        if (parts.Count == 0) return AppRoute.Latest;

        switch (parts[0].ToLowerInvariant())
        {
            case "t":
            case "topic":
                if (parts.Count >= 3 && int.TryParse(parts[2], out var id3))
                {
                    int? post = parts.Count >= 4 && int.TryParse(parts[3], out var p3) ? p3 : null;
                    return AppRoute.Topic(id3, parts[1], post);
                }
                if (parts.Count >= 2 && int.TryParse(parts[1], out var id2))
                {
                    int? post = parts.Count >= 3 && int.TryParse(parts[2], out var p2) ? p2 : null;
                    return AppRoute.Topic(id2, null, post);
                }
                break;
            case "u":
            case "user":
                if (parts.Count >= 2) return AppRoute.User(parts[1]);
                break;
            case "tag":
            case "tags":
                if (parts.Count >= 2) return AppRoute.Tag(parts[1]);
                return AppRoute.Tags;
            case "c":
            case "category":
                if (parts.Count >= 3 && int.TryParse(parts[2], out var cid))
                    return AppRoute.Category(cid, parts[1], parts[1]);
                break;
            case "latest": return AppRoute.Latest;
            case "top": return AppRoute.Top;
            case "unread": return AppRoute.Unread;
            case "new": return AppRoute.New;
            case "search": return AppRoute.Search;
            case "messages":
            case "my":
            case "pm":
                return AppRoute.Messages;
            case "later":
            case "read-later":
            case "readlater":
                return AppRoute.ReadLater;
            case "history": return AppRoute.History;
            default:
                if (int.TryParse(parts[0], out var bareId))
                    return AppRoute.Topic(bareId);
                break;
        }
        return null;
    }
}
