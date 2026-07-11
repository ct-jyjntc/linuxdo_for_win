using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>Process-wide cache of parsed cooked HTML blocks to avoid re-parse on scroll/reopen.</summary>
public static class PostContentCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    private const int MaxEntries = 200;

    private sealed class CacheEntry
    {
        public required List<PostContentParser.Block> Blocks { get; init; }
        public long Touched { get; set; }
    }

    public static List<PostContentParser.Block> GetOrParse(string? html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var key = MakeKey(html, baseUrl);
        if (Cache.TryGetValue(key, out var hit))
        {
            hit.Touched = Environment.TickCount64;
            // Return a shallow copy so callers can own mutation of list structure
            return [.. hit.Blocks];
        }

        var blocks = PostContentParser.Parse(html, baseUrl);
        Cache[key] = new CacheEntry
        {
            Blocks = blocks,
            Touched = Environment.TickCount64
        };
        TrimIfNeeded();
        return [.. blocks];
    }

    public static void Clear() => Cache.Clear();

    public static int Count => Cache.Count;

    private static void TrimIfNeeded()
    {
        if (Cache.Count <= MaxEntries) return;
        var victims = Cache
            .OrderBy(kv => kv.Value.Touched)
            .Take(Cache.Count - MaxEntries + 20)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in victims)
            Cache.TryRemove(k, out _);
    }

    private static string MakeKey(string html, Uri baseUrl)
    {
        // length + short hash is enough for session cache; full hash would be wasteful
        var host = baseUrl.Host ?? "";
        var prefix = html.Length > 64 ? html[..64] : html;
        var payload = $"{host}|{html.Length}|{prefix}|{html[^Math.Min(32, html.Length)..]}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..24];
    }
}
