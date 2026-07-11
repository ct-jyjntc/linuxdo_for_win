using System.Globalization;

namespace LinuxDo.Core.Utilities;

public static class DiscourseDateParser
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd HH:mm:ss"
    ];

    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;

        foreach (var format in Formats)
        {
            if (DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return dto;
        }
        return null;
    }

    public static string Format(DateTimeOffset date)
        => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public static class RelativeDate
{
    private static readonly Dictionary<long, string> Cache = new();
    private static readonly object Gate = new();

    public static string Describe(DateTimeOffset? date)
    {
        if (date is null) return "";
        var bucket = date.Value.ToUnixTimeSeconds() / 60;
        lock (Gate)
        {
            if (Cache.TryGetValue(bucket, out var cached)) return cached;
        }

        var span = DateTimeOffset.UtcNow - date.Value.ToUniversalTime();
        string text;
        if (span.TotalSeconds < 60) text = "刚刚";
        else if (span.TotalMinutes < 60) text = $"{(int)span.TotalMinutes} 分钟前";
        else if (span.TotalHours < 24) text = $"{(int)span.TotalHours} 小时前";
        else if (span.TotalDays < 30) text = $"{(int)span.TotalDays} 天前";
        else if (span.TotalDays < 365) text = $"{(int)(span.TotalDays / 30)} 个月前";
        else text = $"{(int)(span.TotalDays / 365)} 年前";

        lock (Gate)
        {
            if (Cache.Count > 500) Cache.Clear();
            Cache[bucket] = text;
        }
        return text;
    }
}
