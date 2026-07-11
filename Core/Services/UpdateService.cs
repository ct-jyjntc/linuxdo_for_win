using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public sealed class UpdateCheckResult
{
    public enum Kind { Available, UpToDate, Failed }

    public Kind ResultKind { get; init; }
    public string? LatestBuildId { get; init; }
    public string? CurrentBuildId { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? Message { get; init; }

    public static UpdateCheckResult Available(string current, string latest, string url) => new()
    {
        ResultKind = Kind.Available,
        CurrentBuildId = current,
        LatestBuildId = latest,
        ReleaseUrl = url
    };

    public static UpdateCheckResult UpToDate(string current) => new()
    {
        ResultKind = Kind.UpToDate,
        CurrentBuildId = current,
        LatestBuildId = current
    };

    public static UpdateCheckResult Failed(string message) => new()
    {
        ResultKind = Kind.Failed,
        Message = message,
        CurrentBuildId = AppVersion.BuildId
    };
}

/// <summary>GitHub Releases latest-check (mac Settings parity).</summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxDo-Windows");
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = AppVersion.BuildId;
        try
        {
            using var resp = await Http.GetAsync(AppVersion.ReleasesApiUrl, ct);
            if ((int)resp.StatusCode == 404)
                return UpdateCheckResult.Failed("仓库还没有发布任何 Release。");
            if (!resp.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub 返回 HTTP {(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: ct);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return UpdateCheckResult.Failed("无法解析最新版本信息。");

            var latest = AppVersion.NormalizeBuildId(release.TagName);
            var url = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? AppVersion.ReleasesPageUrl
                : release.HtmlUrl!;

            if (AppVersion.IsNewer(latest, current))
                return UpdateCheckResult.Available(current, latest, url);
            return UpdateCheckResult.UpToDate(current);
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("检查更新已取消。");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
