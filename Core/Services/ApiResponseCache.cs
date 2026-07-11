using System.Collections.Concurrent;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>
/// Short-lived GET response cache + global read throttle.
/// Goal: fewer round-trips to linux.do so Cloudflare is less likely to re-challenge.
/// </summary>
public static class ApiResponseCache
{
    private sealed class Entry
    {
        public required byte[] Data { get; init; }
        public required DateTime ExpiresUtc { get; init; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> InFlightStarted = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static DateTime _windowStart = DateTime.UtcNow;
    private static int _windowCount;
    private static DateTime _pausedUntilUtc = DateTime.MinValue;

    /// <summary>Soft cap: max GET-like API calls per rolling window (excluding cache hits).</summary>
    public static int MaxRequestsPerMinute { get; set; } = 28;

    /// <summary>Default TTL for list endpoints.</summary>
    public static TimeSpan DefaultListTtl { get; set; } = TimeSpan.FromSeconds(90);

    public static TimeSpan CategoryTtl { get; set; } = TimeSpan.FromMinutes(10);
    public static TimeSpan TopicTtl { get; set; } = TimeSpan.FromSeconds(60);
    public static TimeSpan SessionTtl { get; set; } = TimeSpan.FromSeconds(45);

    public static bool IsPaused
    {
        get
        {
            lock (Gate) return DateTime.UtcNow < _pausedUntilUtc;
        }
    }

    /// <summary>Pause background/network-heavy work (e.g. while CF challenge is open).</summary>
    public static void Pause(TimeSpan duration)
    {
        lock (Gate)
        {
            var until = DateTime.UtcNow + duration;
            if (until > _pausedUntilUtc) _pausedUntilUtc = until;
        }
        AppLog.Network($"API pause until {_pausedUntilUtc:HH:mm:ss} UTC ({duration.TotalSeconds:0}s)");
    }

    public static void Resume()
    {
        lock (Gate) _pausedUntilUtc = DateTime.MinValue;
        AppLog.Network("API pause cleared");
    }

    public static void Clear()
    {
        Cache.Clear();
        AppLog.Network("API response cache cleared");
    }

    public static bool TryGet(string key, out byte[]? data)
    {
        data = null;
        if (string.IsNullOrEmpty(key)) return false;
        if (!Cache.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow >= entry.ExpiresUtc)
        {
            Cache.TryRemove(key, out _);
            return false;
        }
        data = entry.Data;
        return true;
    }

    public static void Set(string key, byte[] data, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(key) || data.Length == 0) return;
        if (ttl <= TimeSpan.Zero) return;
        // Cap memory: drop oldest-ish by random eviction when large
        if (Cache.Count > 120)
        {
            foreach (var k in Cache.Keys.Take(30).ToList())
                Cache.TryRemove(k, out _);
        }
        Cache[key] = new Entry
        {
            Data = data,
            ExpiresUtc = DateTime.UtcNow + ttl
        };
    }

    public static void InvalidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        foreach (var k in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            Cache.TryRemove(k, out _);
    }

    /// <summary>
    /// Wait if we are over the soft rate limit. Writes should call with skip=true.
    /// </summary>
    public static async Task ThrottleReadAsync(CancellationToken ct = default)
    {
        // While paused (challenge open), still allow a few critical calls but slow them.
        var delayMs = 0;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now < _pausedUntilUtc)
                delayMs = 800;

            if (now - _windowStart > TimeSpan.FromMinutes(1))
            {
                _windowStart = now;
                _windowCount = 0;
            }

            _windowCount++;
            if (_windowCount > MaxRequestsPerMinute)
            {
                // Spread surplus requests into the next window instead of hammering CF.
                var over = _windowCount - MaxRequestsPerMinute;
                delayMs = Math.Max(delayMs, Math.Min(4000, 200 + over * 120));
            }
        }

        if (delayMs > 0)
        {
            AppLog.Network($"API throttle wait {delayMs}ms");
            await Task.Delay(delayMs, ct);
        }
    }

    public static TimeSpan TtlForPath(string path)
    {
        var p = path.Trim('/').ToLowerInvariant();
        if (p.StartsWith("categories", StringComparison.Ordinal)) return CategoryTtl;
        if (p is "session/current.json" or "session/csrf" or "session/csrf.json") return SessionTtl;
        if (p.StartsWith("t/", StringComparison.Ordinal)) return TopicTtl;
        if (p.StartsWith("latest", StringComparison.Ordinal) ||
            p.StartsWith("top", StringComparison.Ordinal) ||
            p.StartsWith("new", StringComparison.Ordinal) ||
            p.StartsWith("unread", StringComparison.Ordinal) ||
            p.StartsWith("c/", StringComparison.Ordinal) ||
            p.StartsWith("tag/", StringComparison.Ordinal) ||
            p.StartsWith("tags", StringComparison.Ordinal) ||
            p.StartsWith("search", StringComparison.Ordinal) ||
            p.StartsWith("notifications", StringComparison.Ordinal) ||
            p.StartsWith("topics/private-messages", StringComparison.Ordinal) ||
            p.StartsWith("bookmarks", StringComparison.Ordinal) ||
            p.StartsWith("drafts", StringComparison.Ordinal) ||
            p.StartsWith("user_actions", StringComparison.Ordinal) ||
            p.StartsWith("u/", StringComparison.Ordinal))
            return DefaultListTtl;
        return TimeSpan.FromSeconds(30);
    }

    public static string CacheKey(Uri url)
        => url.AbsoluteUri;

    public static bool IsCacheableGet(string method, string path)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return false;
        var p = path.Trim('/').ToLowerInvariant();
        // Never cache CSRF mint endpoints as success without revalidation long-term —
        // short SessionTtl is fine for current.json.
        if (p is "session/csrf" or "session/csrf.json") return false;
        return true;
    }
}
