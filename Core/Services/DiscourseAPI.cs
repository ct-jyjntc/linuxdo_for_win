using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public sealed partial class DiscourseAPI
{
    public static DiscourseAPI Shared { get; } = new();

    private Uri _baseUrl = new("https://linux.do");
    private HttpClient _http;
    private string? _apiKey;
    private string? _clientId;
    private string? _csrfToken;
    private bool _allowWebViewFallback = true;
    /// <summary>
    /// After CF, .NET HttpClient often still trips WAF even with cf_clearance (TLS fingerprint).
    /// Prefer the shared WebView for GETs until this time (mac uses same browser context for fetch).
    /// </summary>
    private DateTime _webViewFirstUntilUtc = DateTime.MinValue;
    private readonly object _gate = new();

    private DiscourseAPI()
    {
        CookieSessionBridge.PrimeUserAgent();
        _http = CookieSessionBridge.CreateHttpClient();
    }

    /// <summary>
    /// After CF: enable WebView as FALLBACK only. Do NOT force WebView-first —
    /// hidden WebView2 in-page fetch was status=0 for nearly every call (log 12:02–12:04).
    /// HttpClient + synced cookies is the primary path; WebView is last resort.
    /// </summary>
    public void PreferWebViewFirst(TimeSpan duration)
    {
        // Intentionally do NOT set _webViewFirstUntilUtc. Keep HttpClient primary.
        lock (_gate)
        {
            _allowWebViewFallback = true;
            _webViewFirstUntilUtc = DateTime.MinValue;
        }
        ResetWebViewFailStreak();
        AppLog.Network(
            $"Post-CF: HttpClient primary, WebView fallback enabled ({duration.TotalSeconds:0}s window ignored for first)");
    }

    public void ClearWebViewFirst()
    {
        lock (_gate) _webViewFirstUntilUtc = DateTime.MinValue;
    }

    public Uri CurrentBaseUrl
    {
        get { lock (_gate) return _baseUrl; }
    }

    public Task UpdateBaseUrlAsync(Uri url)
    {
        lock (_gate)
        {
            _baseUrl = url;
            _csrfToken = null;
            _allowWebViewFallback = true;
        }
        return Task.CompletedTask;
    }

    public void SetCredentials(string? apiKey, string? clientId)
    {
        lock (_gate)
        {
            _apiKey = apiKey;
            _clientId = clientId;
        }
    }

    public void ClearCredentials()
    {
        lock (_gate)
        {
            _apiKey = null;
            _clientId = null;
            _csrfToken = null;
        }
    }

    public async Task AdoptWebCookiesAsync(bool force = false, bool invalidateCsrf = false)
    {
        await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(CurrentBaseUrl, force);
        if (invalidateCsrf)
        {
            lock (_gate) _csrfToken = null;
        }
    }

    public void SetPreferWebView(bool value)
    {
        lock (_gate) _allowWebViewFallback = value;
    }

    public void ResetHttpClient()
    {
        lock (_gate)
        {
            _http.Dispose();
            _http = CookieSessionBridge.CreateHttpClient();
            _csrfToken = null;
            _allowWebViewFallback = true;
        }
        AppLog.Network("HttpClient reset after site access change");
    }

    // ── Topics ──────────────────────────────────────────────

    public Task<TopicListResponse> FetchLatestAsync(int page = 0)
        => GetAsync("latest.json", BuildListQuery(page), TopicListResponse.FromJson);

    /// <summary>Network-only probe (no response cache) — used after CF challenge.</summary>
    public async Task<TopicListResponse> FetchLatestUncachedAsync(int page = 0)
    {
        var url = MakeUrl("latest.json", BuildListQuery(page));
        await ApiResponseCache.ThrottleReadAsync();
        var data = await PerformAsync(url, "GET", preferWebView: false);
        if (ResponseInspector.IsProbablyJson(data))
            ApiResponseCache.Set(ApiResponseCache.CacheKey(url), data, ApiResponseCache.TtlForPath("latest.json"));
        return Decode(data, TopicListResponse.FromJson);
    }

    public Task<TopicListResponse> FetchTopAsync(string period = "weekly", int page = 0)
    {
        var q = BuildListQuery(page);
        q["period"] = period;
        return GetAsync("top.json", q, TopicListResponse.FromJson);
    }

    public Task<TopicListResponse> FetchNewAsync(int page = 0)
        => GetAsync("new.json", BuildListQuery(page), TopicListResponse.FromJson);

    public Task<TopicListResponse> FetchUnreadAsync(int page = 0)
        => GetAsync("unread.json", BuildListQuery(page), TopicListResponse.FromJson);

    public Task<TopicListResponse> FetchCategoryTopicsAsync(string slug, int id, int page = 0)
    {
        var path = $"c/{slug}/{id}.json";
        return GetAsync(path, BuildListQuery(page), TopicListResponse.FromJson);
    }

    /// <summary>
    /// Ask Discourse for a larger first page so fewer "load more" hits are needed.
    /// Server may clamp; we still benefit when it honors per_page.
    /// </summary>
    private static Dictionary<string, string> BuildListQuery(int page, int perPage = 50)
    {
        var q = new Dictionary<string, string>
        {
            // Discourse topic lists accept per_page (often capped ~30-50).
            ["per_page"] = Math.Clamp(perPage, 20, 50).ToString()
        };
        if (page > 0) q["page"] = page.ToString();
        return q;
    }

    public async Task<TopicDetailResponse> FetchTopicAsync(int id, int? postNumber = null)
    {
        var path = postNumber is not null ? $"t/{id}/{postNumber}.json" : $"t/{id}.json";
        var url = MakeUrl(path);
        var key = ApiResponseCache.CacheKey(url);
        if (postNumber is null && ApiResponseCache.TryGet(key, out var cached) && cached is not null)
        {
            AppLog.Network($"cache hit topic {id}");
            return Decode(cached, TopicDetailResponse.FromJson);
        }

        await ApiResponseCache.ThrottleReadAsync();
        var data = await PerformAsync(url, "GET", preferWebView: false);
        if (postNumber is null && ResponseInspector.IsProbablyJson(data))
            ApiResponseCache.Set(key, data, ApiResponseCache.TopicTtl);
        return Decode(data, TopicDetailResponse.FromJson);
    }

    public async Task<List<DiscoursePost>> FetchPostsAsync(int topicId, IReadOnlyList<int> postIds)
    {
        if (postIds.Count == 0) return [];
        // Batch in chunks of 50 (Discourse limit) — one request per chunk instead of many.
        const int chunk = 50;
        if (postIds.Count <= chunk)
            return await FetchPostsChunkAsync(topicId, postIds);

        var all = new List<DiscoursePost>(postIds.Count);
        for (var i = 0; i < postIds.Count; i += chunk)
        {
            var slice = postIds.Skip(i).Take(chunk).ToList();
            var part = await FetchPostsChunkAsync(topicId, slice);
            all.AddRange(part);
        }
        return all;
    }

    private async Task<List<DiscoursePost>> FetchPostsChunkAsync(int topicId, IReadOnlyList<int> postIds)
    {
        var qs = string.Join("&", postIds.Select(id => $"post_ids[]={id}"));
        var url = new Uri($"{CurrentBaseUrl.AbsoluteUri.TrimEnd('/')}/t/{topicId}/posts.json?{qs}");
        var key = ApiResponseCache.CacheKey(url);
        if (ApiResponseCache.TryGet(key, out var cached) && cached is not null)
        {
            using var cdoc = JsonDocument.Parse(cached);
            if (cdoc.RootElement.TryGetProperty("post_stream", out var cps))
                return PostStream.FromJson(cps).Posts;
        }

        await ApiResponseCache.ThrottleReadAsync();
        var data = await PerformAsync(url, "GET");
        if (ResponseInspector.IsProbablyJson(data))
            ApiResponseCache.Set(key, data, TimeSpan.FromSeconds(45));
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("post_stream", out var ps))
            return PostStream.FromJson(ps).Posts;
        return [];
    }

    public async Task<List<DiscourseCategory>> FetchCategoriesAsync()
    {
        var data = await PerformAsync(MakeUrl("categories.json"), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("category_list", out var cl) &&
            cl.TryGetProperty("categories", out var cats) &&
            cats.ValueKind == JsonValueKind.Array)
        {
            return JsonFlexible.DecodeLossyArray(cats, DiscourseCategory.FromJson);
        }
        return [];
    }

    public async Task<List<DiscourseTag>> FetchTagsAsync()
    {
        var url = MakeUrl("tags.json");
        // Prefer HttpClient first; WebView only as CF fallback (WebView-first used to deadlock the UI).
        var data = await PerformAsync(url, "GET", preferWebView: false);
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var tags = new List<DiscourseTag>();
        if (root.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            tags = JsonFlexible.DecodeLossyArray(arr, e => DiscourseTag.FromJson(e));
        if (tags.Count == 0 && root.TryGetProperty("extras", out var extras) &&
            extras.TryGetProperty("categories", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groups.EnumerateArray())
            {
                if (g.TryGetProperty("tags", out var gt) && gt.ValueKind == JsonValueKind.Array)
                    tags.AddRange(JsonFlexible.DecodeLossyArray(gt, e => DiscourseTag.FromJson(e)));
            }
        }
        return tags;
    }

    public async Task<TopicListResponse> FetchTagTopicsAsync(string tag, int page = 0)
    {
        var name = tag.Trim();
        if (string.IsNullOrEmpty(name)) throw new APIError.InvalidUrl();
        return await GetAsync($"tag/{name}.json", BuildListQuery(page), TopicListResponse.FromJson);
    }

    // ── Search / notifications / user ───────────────────────

    public async Task<SearchResponse> SearchAsync(string term)
    {
        var q = term.Trim();
        if (string.IsNullOrEmpty(q))
            return new SearchResponse();
        try { await AdoptWebCookiesAsync(); } catch { /* best effort */ }
        var url = MakeUrl("search.json", new() { ["q"] = q });
        var data = await PerformAsync(url, "GET", preferWebView: false);
        return Decode(data, SearchResponse.FromJson);
    }

    public async Task<List<DiscourseNotification>> FetchNotificationsAsync(int limit = 60)
    {
        var data = await PerformAsync(MakeUrl("notifications.json", new() { ["limit"] = limit.ToString() }), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("notifications", out var arr) && arr.ValueKind == JsonValueKind.Array)
            return JsonFlexible.DecodeLossyArray(arr, e => DiscourseNotification.FromJson(e));
        return [];
    }

    public Task MarkNotificationsReadAsync()
        => PutRawJsonAsync("notifications/mark-read.json", new Dictionary<string, object?>());

    public async Task<CurrentUser?> FetchCurrentUserAsync()
    {
        var data = await PerformAsync(MakeUrl("session/current.json"), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("current_user", out var cu))
            return CurrentUser.FromJson(cu);
        return null;
    }

    public async Task<UserProfile> FetchUserProfileAsync(string username)
    {
        var data = await PerformAsync(MakeUrl($"u/{username}.json"), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("user", out var user))
            return UserProfile.FromJson(user);
        throw new APIError.Decoding(new Exception("missing user"));
    }

    public async Task<UserProfile> FetchUserCardAsync(string username)
    {
        var data = await PerformAsync(MakeUrl($"u/{username}/card.json"), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("user", out var user))
            return UserProfile.FromJson(user);
        throw new APIError.Decoding(new Exception("missing user"));
    }

    public async Task<TopicListResponse> FetchUserTopicsAsync(string username, int page = 0)
    {
        var q = page > 0 ? new Dictionary<string, string> { ["page"] = page.ToString() } : null;
        return await GetAsync($"topics/created-by/{username}.json", q, TopicListResponse.FromJson);
    }

    public async Task<List<DiscourseBookmark>> FetchBookmarksAsync(string username)
    {
        try
        {
            var data = await PerformAsync(MakeUrl($"u/{username}/bookmarks.json"), "GET");
            var list = ParseBookmarks(data);
            if (list.Count > 0) return list;
        }
        catch (Exception ex)
        {
            AppLog.Warning("network", $"user bookmarks failed: {ex.Message}");
        }
        var data2 = await PerformAsync(MakeUrl("bookmarks.json"), "GET");
        return ParseBookmarks(data2);
    }

    private static List<DiscourseBookmark> ParseBookmarks(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("user_bookmark_list", out var ubl) &&
            ubl.TryGetProperty("bookmarks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            return JsonFlexible.DecodeLossyArray(arr, e => DiscourseBookmark.FromJson(e));
        if (root.TryGetProperty("bookmarks", out var b) && b.ValueKind == JsonValueKind.Array)
            return JsonFlexible.DecodeLossyArray(b, e => DiscourseBookmark.FromJson(e));
        return [];
    }

    // ── Private messages ────────────────────────────────────

    public Task<TopicListResponse> FetchPrivateMessagesAsync(string username, int page = 0)
        => GetAsync($"topics/private-messages/{username}.json",
            page > 0 ? new() { ["page"] = page.ToString() } : null, TopicListResponse.FromJson);

    public Task<TopicListResponse> FetchPrivateMessagesSentAsync(string username, int page = 0)
        => GetAsync($"topics/private-messages-sent/{username}.json",
            page > 0 ? new() { ["page"] = page.ToString() } : null, TopicListResponse.FromJson);

    public Task<TopicListResponse> FetchPrivateMessagesArchiveAsync(string username, int page = 0)
        => GetAsync($"topics/private-messages-archive/{username}.json",
            page > 0 ? new() { ["page"] = page.ToString() } : null, TopicListResponse.FromJson);

    public Task ArchivePrivateMessageAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/archive-message", new Dictionary<string, object?>());

    public Task MovePrivateMessageToInboxAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/move-to-inbox", new Dictionary<string, object?>());

    // ── Write actions ───────────────────────────────────────

    public async Task<CreatePostResponse> CreateTopicAsync(string title, string raw, int? categoryId, IReadOnlyList<string>? tags = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["raw"] = raw };
        if (categoryId is not null) body["category"] = categoryId;
        if (tags is { Count: > 0 }) body["tags"] = tags.ToList();
        var data = await PostRawJsonAsync("posts.json", body);
        return Decode(data, CreatePostResponse.FromJson);
    }

    public async Task<CreatePostResponse> ReplyAsync(int topicId, string raw, int? replyToPostNumber = null)
    {
        var body = new Dictionary<string, object?> { ["topic_id"] = topicId, ["raw"] = raw };
        if (replyToPostNumber is not null) body["reply_to_post_number"] = replyToPostNumber;
        var data = await PostRawJsonAsync("posts.json", body);
        return Decode(data, CreatePostResponse.FromJson);
    }

    public async Task<CreatePostResponse> CreatePrivateMessageAsync(string title, string raw, IReadOnlyList<string> recipients)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["raw"] = raw,
            ["archetype"] = "private_message",
            ["target_recipients"] = string.Join(",", recipients)
        };
        var data = await PostRawJsonAsync("posts.json", body);
        return Decode(data, CreatePostResponse.FromJson);
    }

    public Task EditPostAsync(int postId, string raw, string? editReason = null)
    {
        var post = new Dictionary<string, object?> { ["raw"] = raw };
        if (!string.IsNullOrEmpty(editReason)) post["edit_reason"] = editReason;
        return PutRawJsonAsync($"posts/{postId}.json", new Dictionary<string, object?> { ["post"] = post });
    }

    public Task DeletePostAsync(int postId) => DeleteAsync($"posts/{postId}.json");
    public Task RecoverPostAsync(int postId) => PutRawJsonAsync($"posts/{postId}/recover.json", new Dictionary<string, object?>());

    public Task LikePostAsync(int postId)
        => PostRawJsonAsync("post_actions.json", new Dictionary<string, object?>
        {
            ["id"] = postId, ["post_action_type_id"] = 2, ["flag_topic"] = false
        });

    public Task UnlikePostAsync(int postId)
        => DeleteAsync($"post_actions/{postId}", new() { ["post_action_type_id"] = "2" });

    public Task SetTopicNotificationLevelAsync(int topicId, TopicNotificationLevel level)
        => PutRawJsonAsync($"t/{topicId}/notifications.json",
            new Dictionary<string, object?> { ["notification_level"] = (int)level });

    public Task CloseTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "closed", ["enabled"] = "true" });
    public Task OpenTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "closed", ["enabled"] = "false" });
    public Task PinTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "pinned", ["enabled"] = "true" });
    public Task UnpinTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "pinned", ["enabled"] = "false" });
    public Task ArchiveTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "archived", ["enabled"] = "true" });
    public Task UnarchiveTopicAsync(int topicId)
        => PutRawJsonAsync($"t/{topicId}/status.json", new Dictionary<string, object?> { ["status"] = "archived", ["enabled"] = "false" });

    private int _lastTimingsTopicId;
    private DateTime _lastTimingsUtc = DateTime.MinValue;

    public async Task MarkTopicReadAsync(int topicId, int postNumber)
    {
        // Best-effort only: never run write-recovery / WebView / challenge storms for timings.
        if (SiteAccessStore.Current.NeedsChallenge || WebViewAPIClient.Shared.IsChallengeUiActive)
            return;
        if (ApiResponseCache.IsPaused)
            return;
        if (!UserSessionStore.Current.IsLoggedIn ||
            !CookieSessionBridge.HasLoginCookie(CurrentBaseUrl))
            return;
        // Debounce: at most one timings POST per topic per 20s (rapid topic hopping was noisy).
        if (topicId == _lastTimingsTopicId &&
            DateTime.UtcNow - _lastTimingsUtc < TimeSpan.FromSeconds(20))
            return;
        try
        {
            var url = MakeUrl("topics/timings");
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["topic_id"] = topicId,
                ["topic_time"] = 1000,
                ["timings"] = new Dictionary<string, object?> { [postNumber.ToString()] = 1000 }
            });
            _ = await PerformViaHttpAsync(url, "POST", body, "application/json", needsCsrf: true, acceptJson: true);
            _lastTimingsTopicId = topicId;
            _lastTimingsUtc = DateTime.UtcNow;
        }
        catch { /* ignore */ }
    }

    public async Task<int?> BookmarkPostAsync(int postId, string? name = null, DateTimeOffset? reminderAt = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["bookmarkable_id"] = postId,
            ["bookmarkable_type"] = "Post"
        };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name.Trim();
        if (reminderAt is not null) body["reminder_at"] = DiscourseDateParser.Format(reminderAt.Value);
        var data = await PostRawJsonAsync("bookmarks.json", body);
        return ParseBookmarkId(data);
    }

    public async Task<int?> BookmarkTopicAsync(int topicId, string? name = null, DateTimeOffset? reminderAt = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["bookmarkable_id"] = topicId,
            ["bookmarkable_type"] = "Topic"
        };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name.Trim();
        if (reminderAt is not null) body["reminder_at"] = DiscourseDateParser.Format(reminderAt.Value);
        var data = await PostRawJsonAsync("bookmarks.json", body);
        return ParseBookmarkId(data);
    }

    public Task UpdateBookmarkAsync(int bookmarkId, string? name = null, DateTimeOffset? reminderAt = null, bool clearReminder = false)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name.Trim();
        if (clearReminder) body["reminder_at"] = null;
        else if (reminderAt is not null) body["reminder_at"] = DiscourseDateParser.Format(reminderAt.Value);
        if (body.Count == 0) return Task.CompletedTask;
        return PutRawJsonAsync($"bookmarks/{bookmarkId}.json", body);
    }

    public Task DeleteBookmarkAsync(int bookmarkId) => DeleteAsync($"bookmarks/{bookmarkId}");

    private static int? ParseBookmarkId(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id)) return JsonFlexible.GetInt(id);
            if (root.TryGetProperty("bookmark", out var b) && b.TryGetProperty("id", out var bid))
                return JsonFlexible.GetInt(bid);
        }
        catch { /* ignore */ }
        return null;
    }

    public async Task ToggleReactionAsync(int postId, string reaction)
    {
        await PutRawJsonAsync(
            $"discourse-reactions/posts/{postId}/custom-reactions/{Uri.EscapeDataString(reaction)}/toggle.json",
            new Dictionary<string, object?>());
    }

    public async Task<DiscoursePoll?> VotePollAsync(int postId, string pollName, IReadOnlyList<string> options)
    {
        var data = await PutRawJsonAsync("polls/vote", new Dictionary<string, object?>
        {
            ["post_id"] = postId,
            ["poll_name"] = pollName,
            ["options"] = options.ToList()
        });
        return ParsePoll(data);
    }

    public async Task<DiscoursePoll?> RemovePollVoteAsync(int postId, string pollName)
    {
        var url = MakeUrl("polls/vote");
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["post_id"] = postId,
            ["poll_name"] = pollName
        });
        var data = await PerformAsync(url, "DELETE", body, "application/json", needsCsrf: true);
        return ParsePoll(data);
    }

    private static DiscoursePoll? ParsePoll(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("poll", out var p) && p.ValueKind == JsonValueKind.Object)
                return DiscoursePoll.FromJson(p);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out _))
                return DiscoursePoll.FromJson(doc.RootElement);
        }
        catch { /* ignore */ }
        return null;
    }

    public async Task<List<UserSearchItem>> SearchUsersAsync(string term)
    {
        var q = term.Trim();
        if (string.IsNullOrEmpty(q)) return [];
        try
        {
            var data = await PerformAsync(MakeUrl("u/search/users", new()
            {
                ["term"] = q,
                ["include_groups"] = "false"
            }), "GET");
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(users, e => UserSearchItem.FromJson(e));
        }
        catch
        {
            try
            {
                var data = await PerformAsync(MakeUrl("user-search", new() { ["term"] = q }), "GET");
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
                    return JsonFlexible.DecodeLossyArray(users, e => UserSearchItem.FromJson(e));
            }
            catch { /* ignore */ }
        }
        return [];
    }

    public async Task ExportTopicMarkdownAsync(int topicId)
    {
        await Task.CompletedTask;
    }

    // ── Nested replies / boost / reaction users / social ──

    public async Task<List<NestedPostNode>> FetchNestedRootsAsync(int topicId, int page = 0)
    {
        var data = await PerformAsync(
            MakeUrl($"n/topic/{topicId}.json", new() { ["sort"] = "old", ["page"] = page.ToString() }),
            "GET");
        return ParseNestedNodes(data);
    }

    public async Task<List<NestedPostNode>> FetchNestedChildrenAsync(int topicId, int postNumber, int page = 0)
    {
        var data = await PerformAsync(
            MakeUrl($"n/topic/{topicId}/children/{postNumber}.json",
                new() { ["sort"] = "old", ["page"] = page.ToString(), ["depth"] = "1" }),
            "GET");
        return ParseNestedNodes(data);
    }

    private static List<NestedPostNode> ParseNestedNodes(byte[] data)
    {
        var results = new List<NestedPostNode>();
        try
        {
            using var doc = JsonDocument.Parse(data);
            var arrays = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arrays.Add(doc.RootElement);
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "posts", "roots", "children", "post_stream" })
                {
                    if (!doc.RootElement.TryGetProperty(key, out var el)) continue;
                    if (el.ValueKind == JsonValueKind.Array) arrays.Add(el);
                    else if (el.ValueKind == JsonValueKind.Object &&
                             el.TryGetProperty("posts", out var nested) &&
                             nested.ValueKind == JsonValueKind.Array)
                        arrays.Add(nested);
                }
            }

            foreach (var arr in arrays)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var postNumber = JsonFlexible.Prop(item, "post_number", JsonFlexible.GetInt)
                                     ?? JsonFlexible.Prop(item, "number", JsonFlexible.GetInt)
                                     ?? 0;
                    if (postNumber <= 0) continue;
                    var excerpt = JsonFlexible.Prop(item, "excerpt", JsonFlexible.GetString)
                                  ?? JsonFlexible.Prop(item, "cooked", JsonFlexible.GetString)
                                  ?? JsonFlexible.Prop(item, "raw", JsonFlexible.GetString)
                                  ?? "";
                    if (!string.IsNullOrEmpty(excerpt) && excerpt.Contains('<'))
                        excerpt = HtmlText.PlainText(excerpt);
                    results.Add(new NestedPostNode
                    {
                        PostNumber = postNumber,
                        PostId = JsonFlexible.Prop(item, "id", JsonFlexible.GetInt),
                        Username = JsonFlexible.Prop(item, "username", JsonFlexible.GetString),
                        Excerpt = excerpt,
                        ReplyCount = JsonFlexible.Prop(item, "reply_count", JsonFlexible.GetInt)
                                     ?? JsonFlexible.Prop(item, "children_count", JsonFlexible.GetInt)
                                     ?? 0
                    });
                }
            }
        }
        catch { /* ignore */ }

        var seen = new HashSet<int>();
        return results.Where(r => seen.Add(r.PostNumber)).ToList();
    }

    public async Task<PostBoost> CreateBoostAsync(int postId, string raw)
    {
        var data = await PostRawJsonAsync($"discourse-boosts/posts/{postId}/boosts",
            new Dictionary<string, object?> { ["raw"] = raw });
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("boost", out var b) && b.ValueKind == JsonValueKind.Object)
                return PostBoost.FromJson(b);
            if (doc.RootElement.TryGetProperty("id", out _))
                return PostBoost.FromJson(doc.RootElement);
        }
        catch { /* fallthrough */ }
        throw new APIError.ServerMessage("Boost 创建成功但响应无法解析");
    }

    public Task DeleteBoostAsync(int boostId)
        => DeleteAsync($"discourse-boosts/boosts/{boostId}");

    public async Task<List<ReactionUsersGroup>> FetchReactionUsersAsync(int postId)
    {
        var data = await PerformAsync(
            MakeUrl($"discourse-reactions/posts/{postId}/reactions-users.json"), "GET");
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("reaction_users", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(arr, e => ReactionUsersGroup.FromJson(e));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(doc.RootElement, e => ReactionUsersGroup.FromJson(e));
        }
        catch { /* ignore */ }
        return [];
    }

    public Task SetUserNotificationLevelAsync(string username, UserNotificationLevel level, string? expiringAt = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["notification_level"] = level.ApiValue()
        };
        if (!string.IsNullOrEmpty(expiringAt))
            body["expiring_at"] = expiringAt;
        return PutRawJsonAsync($"u/{username}/notification_level.json", body);
    }

    public async Task<List<FollowUser>> FetchFollowingAsync(string username)
        => await FetchFollowListAsync($"u/{username}/follow/following");

    public async Task<List<FollowUser>> FetchFollowersAsync(string username)
        => await FetchFollowListAsync($"u/{username}/follow/followers");

    private async Task<List<FollowUser>> FetchFollowListAsync(string path)
    {
        try
        {
            var data = await PerformAsync(MakeUrl(path), "GET");
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(doc.RootElement, e => FollowUser.FromJson(e));
            foreach (var key in new[] { "users", "following", "followers" })
            {
                if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return JsonFlexible.DecodeLossyArray(arr, e => FollowUser.FromJson(e));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("network", "follow list: " + ex.Message);
        }
        return [];
    }

    public Task AcceptAnswerAsync(int postId)
        => PostRawJsonAsync("solution/accept", new Dictionary<string, object?> { ["id"] = postId });

    public Task UnacceptAnswerAsync(int postId)
        => PostRawJsonAsync("solution/unaccept", new Dictionary<string, object?> { ["id"] = postId });

    public async Task<(int count, bool userCreated)> ToggleSharedIssueAsync(int topicId)
    {
        var data = await PostRawJsonAsync("solution/shared_issue",
            new Dictionary<string, object?> { ["topic_id"] = topicId });
        try
        {
            using var doc = JsonDocument.Parse(data);
            var count = JsonFlexible.Prop(doc.RootElement, "count", JsonFlexible.GetInt) ?? 0;
            var user = JsonFlexible.Prop(doc.RootElement, "user_created_shared_issue", JsonFlexible.GetBool) ?? false;
            return (count, user);
        }
        catch { return (0, false); }
    }

    public Task FlagPostAsync(int postId, int flagType, string? message = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = postId,
            ["post_action_type_id"] = flagType,
            ["flag_topic"] = false
        };
        if (!string.IsNullOrEmpty(message)) body["message"] = message;
        return PostRawJsonAsync("post_actions.json", body);
    }

    public async Task FollowUserAsync(string username)
        => await PutRawJsonAsync($"follow/{username}", new Dictionary<string, object?>());

    public Task UnfollowUserAsync(string username) => DeleteAsync($"follow/{username}");

    // ── Server drafts ───────────────────────────────────────

    public async Task<List<ServerDraft>> FetchDraftsAsync(int offset = 0, int limit = 50)
    {
        var data = await PerformAsync(MakeUrl("drafts.json", new()
        {
            ["offset"] = Math.Max(0, offset).ToString(),
            ["limit"] = Math.Clamp(limit, 1, 100).ToString()
        }), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("drafts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            return JsonFlexible.DecodeLossyArray(arr, e => ServerDraft.FromJson(e));
        return [];
    }

    public async Task<int> SaveDraftAsync(string key, ServerDraftPayload payload, int sequence = 0, bool forceSave = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["draft_key"] = key,
            ["data"] = payload.ToJsonString(),
            ["sequence"] = sequence
        };
        if (forceSave) body["force_save"] = true;

        try
        {
            var data = await PostRawJsonAsync("drafts.json", body);
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("draft_sequence", out var seq))
                return JsonFlexible.GetInt(seq) ?? sequence + 1;
            return sequence + 1;
        }
        catch (APIError.Http http) when (http.Status == 409 && !forceSave)
        {
            return await SaveDraftAsync(key, payload, sequence, forceSave: true);
        }
    }

    public async Task DeleteDraftAsync(string key, int sequence = 0)
    {
        try
        {
            await DeleteAsync($"drafts/{key}.json", new() { ["sequence"] = sequence.ToString() });
        }
        catch (APIError.Http http) when (http.Status == 404)
        {
            // already gone
        }
    }

    // ── Invites ─────────────────────────────────────────────

    public async Task<List<InviteLink>> FetchPendingInvitesAsync(string username)
    {
        var data = await PerformAsync(MakeUrl($"u/{username}/invited/pending"), "GET");
        return ParseInvites(data);
    }

    public async Task<InviteLink> CreateInviteLinkAsync(int maxRedemptions = 5, DateTimeOffset? expiresAt = null, string? description = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["max_redemptions_allowed"] = Math.Max(1, maxRedemptions)
        };
        if (expiresAt is not null)
            body["expires_at"] = DiscourseDateParser.Format(expiresAt.Value);
        if (!string.IsNullOrWhiteSpace(description))
            body["description"] = description.Trim();

        var data = await PostRawJsonAsync("invites", body);
        var list = ParseInvites(data);
        if (list.Count > 0) return list[0];
        try
        {
            using var doc = JsonDocument.Parse(data);
            return InviteLink.FromJson(doc.RootElement);
        }
        catch
        {
            throw new APIError.ServerMessage("邀请链接创建成功但响应无法解析");
        }
    }

    private static List<InviteLink> ParseInvites(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(root, e => InviteLink.FromJson(e));
            foreach (var key in new[] { "invites", "pending_invites" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = JsonFlexible.DecodeLossyArray(arr, e => InviteLink.FromJson(e));
                    if (list.Count > 0) return list;
                }
            }
            if (root.TryGetProperty("invite", out var one) && one.ValueKind == JsonValueKind.Object)
                return [InviteLink.FromJson(one)];
            if (root.TryGetProperty("invite_link", out var linkEl))
            {
                var link = JsonFlexible.GetString(linkEl);
                if (!string.IsNullOrEmpty(link))
                    return [new InviteLink { Link = link! }];
            }
            if (root.TryGetProperty("id", out _) || root.TryGetProperty("invite_link", out _))
                return [InviteLink.FromJson(root)];
        }
        catch { /* ignore */ }
        return [];
    }

    // ── Templates / summary / activity ──────────────────────

    public async Task<List<PostTemplate>> FetchTemplatesAsync()
    {
        try
        {
            var data = await PerformAsync(MakeUrl("discourse_templates"), "GET");
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("templates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(arr, e => PostTemplate.FromJson(e));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonFlexible.DecodeLossyArray(doc.RootElement, e => PostTemplate.FromJson(e));
        }
        catch (Exception ex)
        {
            AppLog.Warning("network", "templates: " + ex.Message);
        }
        return [];
    }

    public async Task<UserSummaryResponse> FetchUserSummaryAsync(string username)
    {
        var data = await PerformAsync(MakeUrl($"u/{username}/summary.json"), "GET");
        using var doc = JsonDocument.Parse(data);
        var resp = new UserSummaryResponse();
        if (doc.RootElement.TryGetProperty("user_summary", out var sum) && sum.ValueKind == JsonValueKind.Object)
            resp.Summary = UserSummary.FromJson(sum);
        if (doc.RootElement.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
            resp.Badges = JsonFlexible.DecodeLossyArray(badges, e => UserBadge.FromJson(e));
        return resp;
    }

    public async Task<List<UserAction>> FetchUserActionsAsync(string username, UserActionFilter filter = UserActionFilter.All, int offset = 0, int limit = 30)
    {
        var query = new Dictionary<string, string>
        {
            ["username"] = username,
            ["offset"] = Math.Max(0, offset).ToString(),
            ["limit"] = Math.Clamp(limit, 1, 60).ToString()
        };
        if (filter != UserActionFilter.All)
            query["filter"] = ((int)filter).ToString();
        var data = await PerformAsync(MakeUrl("user_actions.json", query), "GET");
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("user_actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            return JsonFlexible.DecodeLossyArray(arr, e => UserAction.FromJson(e));
        return [];
    }

    public async Task UploadImageAsync(byte[] data, string fileName, string mimeType)
    {
        await PrepareWriteSessionAsync();
        // Implemented as multipart below — callers use UploadImageDataAsync
        _ = await UploadImageDataAsync(data, fileName, mimeType);
    }

    public async Task<UploadResponse> UploadImageDataAsync(byte[] data, string fileName, string mimeType)
    {
        await PrepareWriteSessionAsync();
        if (data.Length == 0) throw new APIError.ServerMessage("图片数据为空");

        var attempts = new[]
        {
            new Dictionary<string, string> { ["type"] = "composer", ["synchronous"] = "true" },
            new Dictionary<string, string> { ["upload_type"] = "composer", ["synchronous"] = "true" },
            new Dictionary<string, string> { ["type"] = "composer" }
        };

        Exception last = new APIError.ServerMessage("上传失败");
        foreach (var fields in attempts)
        {
            try
            {
                return await UploadMultipartAsync("uploads.json", fileName, mimeType, data, fields);
            }
            catch (Exception ex)
            {
                last = ex;
                AppLog.Warning("network", $"Upload attempt failed: {ex.Message}");
            }
        }
        throw last;
    }

    // ── CSRF ────────────────────────────────────────────────

    /// <summary>
    /// Cookie-session writes need a CSRF token bound to the current _forum_session.
    /// Always force-sync WebView cookies first so HttpClient and browser share the same jar.
    /// </summary>
    public async Task PrepareWriteSessionAsync()
    {
        // Sync cookies; only invalidate CSRF if we have none yet (avoid thrashing session/csrf).
        string? existing;
        lock (_gate) existing = _csrfToken;
        await AdoptWebCookiesAsync(force: true, invalidateCsrf: string.IsNullOrEmpty(existing));

        string? apiKey;
        lock (_gate) apiKey = _apiKey;
        if (apiKey is not null)
            return;

        if (!CookieSessionBridge.HasSessionCookie(CurrentBaseUrl))
        {
            try { await WebViewAPIClient.Shared.EnsureWarmAsync(CurrentBaseUrl, 5); } catch { /* ignore */ }
            await AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
        }

        // Reuse cached CSRF when present — force refresh only on 403 write path.
        await EnsureCsrfTokenAsync(forceRefresh: false);
        AppLog.Network("Write session ready (csrf=" +
                       (string.IsNullOrEmpty(_csrfToken) ? "missing" : "ok") +
                       ", sessionCookie=" + CookieSessionBridge.HasSessionCookie(CurrentBaseUrl) + ")");
    }

    public Task<string> EnsureCsrfTokenAsync() => EnsureCsrfTokenAsync(forceRefresh: false);

    public async Task<string> EnsureCsrfTokenAsync(bool forceRefresh)
    {
        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_csrfToken)) return _csrfToken!;
            }
        }
        else
        {
            lock (_gate) _csrfToken = null;
        }

        await AdoptWebCookiesAsync(force: forceRefresh, invalidateCsrf: false);

        // Prefer HttpClient /session/csrf with the shared cookie jar (mac URLSession path).
        // WebView fetch for CSRF was causing status=0 storms while the document was navigating.
        string? token = await FetchCsrfFromEndpointAsync()
                        ?? await FetchCsrfFromSessionCurrentAsync();

        // Meta tag from current document only if already warm — no navigation.
        if (token is null && WebViewAPIClient.Shared.IsWarmDocument)
        {
            try
            {
                token = await WebViewAPIClient.Shared.ExtractCsrfTokenAsync(CurrentBaseUrl);
            }
            catch (Exception ex)
            {
                AppLog.Warning("csrf", "WebView meta extract: " + ex.Message);
            }
        }

        // Last resort: homepage HTML via WebView only if not challenge UI.
        if (token is null && !WebViewAPIClient.Shared.IsChallengeUiActive)
            token = await FetchCsrfFromHomepageHtmlAsync();

        if (string.IsNullOrEmpty(token))
            throw new APIError.ServerMessage("无法获取 CSRF Token。请先完成站点验证并重新登录后再试。");

        lock (_gate) _csrfToken = token;
        AppLog.Network("CSRF acquired (" + token.Length + " chars)");
        return token!;
    }

    private async Task<string?> FetchCsrfFromEndpointAsync()
    {
        foreach (var path in new[] { "session/csrf", "session/csrf.json" })
        {
            try
            {
                var data = await PerformViaHttpAsync(MakeUrl(path), "GET", null, null, needsCsrf: false, acceptJson: true);
                var t = ParseCsrfJson(data);
                if (t is not null) return t;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private async Task<string?> FetchCsrfFromSessionCurrentAsync()
    {
        try
        {
            var data = await PerformViaHttpAsync(MakeUrl("session/current.json"), "GET", null, null, needsCsrf: false, acceptJson: true);
            return ParseCsrfJson(data);
        }
        catch { return null; }
    }

    private async Task<string?> FetchCsrfFromHomepageHtmlAsync()
    {
        try
        {
            var data = await WebViewAPIClient.Shared.FetchAsync(
                CurrentBaseUrl, "GET",
                new Dictionary<string, string> { ["Accept"] = CookieSessionBridge.AcceptHtml },
                expectJson: false);
            var html = Encoding.UTF8.GetString(data);
            return ExtractCsrf(html);
        }
        catch { return null; }
    }

    private static string? ParseCsrfJson(byte[] data)
    {
        if (!ResponseInspector.IsProbablyJson(data)) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("csrf", out var c))
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (doc.RootElement.TryGetProperty("csrf_token", out var c2))
            {
                var s = c2.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ExtractCsrf(string html)
    {
        var patterns = new[]
        {
            """name=["']csrf-token["']\s+content=["']([^"']+)["']""",
            """content=["']([^"']+)["']\s+name=["']csrf-token["']""",
            """csrf["']\s*:\s*["']([^"']+)["']"""
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(html, p, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1 && !string.IsNullOrEmpty(m.Groups[1].Value))
                return m.Groups[1].Value;
        }
        return null;
    }

    // ── HTTP helpers ────────────────────────────────────────

    private async Task<T> GetAsync<T>(string path, Dictionary<string, string>? query, Func<JsonElement, T> map)
    {
        var url = MakeUrl(path, query);
        var cacheable = ApiResponseCache.IsCacheableGet("GET", path);
        var key = ApiResponseCache.CacheKey(url);
        if (cacheable && ApiResponseCache.TryGet(key, out var cached) && cached is not null)
        {
            AppLog.Network($"cache hit GET {url.AbsolutePath}");
            return Decode(cached, map);
        }

        await ApiResponseCache.ThrottleReadAsync();
        var data = await PerformAsync(url, "GET");
        if (cacheable && ResponseInspector.IsProbablyJson(data))
            ApiResponseCache.Set(key, data, ApiResponseCache.TtlForPath(path));
        return Decode(data, map);
    }

    private static T Decode<T>(byte[] data, Func<JsonElement, T> map)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return map(doc.RootElement);
        }
        catch (Exception ex)
        {
            if (ResponseInspector.LooksLikeCloudflare(data, 403) ||
                (ResponseInspector.LooksLikeHtml(data) && !ResponseInspector.IsProbablyJson(data)))
                throw new APIError.CloudflareChallenge();
            if (!ResponseInspector.IsProbablyJson(data))
                throw new APIError.NonJsonResponse();
            AppLog.Error("network", $"Decode failed: {ex.Message} body={Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(180, data.Length)))}");
            throw new APIError.Decoding(ex);
        }
    }

    private async Task DeleteAsync(string path, Dictionary<string, string>? query = null)
    {
        var url = MakeUrl(path, query);
        await PerformWriteAsync(url, "DELETE", null, null);
    }

    private async Task<byte[]> PostRawJsonAsync(string path, Dictionary<string, object?> body)
    {
        var url = MakeUrl(path);
        var bodyData = JsonSerializer.SerializeToUtf8Bytes(body);
        return await PerformWriteAsync(url, "POST", bodyData, "application/json");
    }

    private async Task<byte[]> PutRawJsonAsync(string path, Dictionary<string, object?> body)
    {
        var url = MakeUrl(path);
        var bodyData = JsonSerializer.SerializeToUtf8Bytes(body);
        return await PerformWriteAsync(url, "PUT", bodyData, "application/json");
    }

    /// <summary>
    /// Mutating calls: cookie sync + CSRF, HttpClient first, then one forced CSRF refresh,
    /// then WebView fallback (same cookie profile as CF challenge).
    /// </summary>
    private async Task<byte[]> PerformWriteAsync(
        Uri url, string method, byte[]? body, string? contentType)
    {
        // Ensure session cookies are in the HttpClient jar before the first write attempt.
        try
        {
            await AdoptWebCookiesAsync(force: false, invalidateCsrf: false);
        }
        catch { /* best effort */ }

        try
        {
            return await PerformAsync(url, method, body, contentType, needsCsrf: true, acceptJson: true);
        }
        catch (APIError error) when (error is APIError.Forbidden or APIError.Unauthorized
                                     || (error is APIError.Http h && h.Status is 403 or 422)
                                     || error.IsChallengeRelated)
        {
            // Real permission denials ("没有权限") are not CSRF/CF — don't storm retries.
            var msg = error.Message ?? "";
            if (msg.Contains("没有权限", StringComparison.Ordinal) ||
                msg.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warning("network", $"Write {method} {url.AbsolutePath} permission denied — no retry");
                throw;
            }

            AppLog.Warning("network", $"Write {method} {url.AbsolutePath} failed ({error.Message}); resync + retry once");

            await AdoptWebCookiesAsync(force: true, invalidateCsrf: true);
            try { await EnsureCsrfTokenAsync(forceRefresh: true); }
            catch (Exception csrfEx)
            {
                AppLog.Warning("csrf", "refresh before write retry: " + csrfEx.Message);
            }

            // One Http retry only — WebView write path was status=0 noise.
            return await PerformViaHttpAsync(url, method, body, contentType, needsCsrf: true, acceptJson: true);
        }
    }

    private static APIError MapWriteError(APIError primary, APIError fallback)
    {
        if (primary is APIError.ServerMessage or APIError.Http { Status: > 0 })
            return primary;
        if (fallback is APIError.ServerMessage or APIError.Http { Status: > 0 })
            return fallback;
        if (primary.IsChallengeRelated || fallback.IsChallengeRelated)
            return new APIError.CloudflareChallenge();
        return primary;
    }

    private async Task<UploadResponse> UploadMultipartAsync(
        string path, string fileName, string mimeType, byte[] fileData, Dictionary<string, string> fields)
    {
        await AdoptWebCookiesAsync(force: true);
        var boundary = "Boundary-" + Guid.NewGuid().ToString("N");
        using var content = new MultipartFormDataContent(boundary);
        foreach (var (k, v) in fields)
            content.Add(new StringContent(v), k);
        var fileContent = new ByteArrayContent(fileData);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(fileContent, "file", fileName);

        var url = MakeUrl(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await ApplyHeadersAsync(request, needsCsrf: true, acceptJson: true);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new APIError.Network(ex);
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
            throw new APIError.Http((int)response.StatusCode, ExtractErrorMessage(data));

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var msgs = errors.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0);
                var joined = string.Join("；", msgs);
                if (!string.IsNullOrEmpty(joined)) throw new APIError.ServerMessage(joined);
            }
            var decoded = UploadResponse.FromJson(doc.RootElement);
            if (decoded.Url is null && decoded.ShortUrl is null && decoded.ShortPath is null)
                throw new APIError.ServerMessage("服务器未返回图片地址");
            return decoded;
        }
        catch (APIError) { throw; }
        catch (Exception ex) { throw new APIError.Decoding(ex); }
    }

    private Uri MakeUrl(string path, Dictionary<string, string>? query = null)
    {
        var clean = path.Trim('/');
        var encoded = string.Join('/', clean.Split('/').Select(Uri.EscapeDataString));
        // EscapeDataString is too aggressive for path — Discourse usernames need '.' '_' '-'
        // Re-decode common safe chars
        encoded = encoded.Replace("%2E", ".", StringComparison.Ordinal)
            .Replace("%2D", "-", StringComparison.Ordinal)
            .Replace("%5F", "_", StringComparison.Ordinal);

        var baseStr = CurrentBaseUrl.AbsoluteUri.TrimEnd('/');
        var urlStr = string.IsNullOrEmpty(encoded) ? baseStr : $"{baseStr}/{encoded}";
        if (query is { Count: > 0 })
        {
            var qs = string.Join("&", query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            urlStr += "?" + qs;
        }
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var url))
            throw new APIError.InvalidUrl();
        return url;
    }

    // Prevent stampede: many concurrent GETs all falling into WebView after one CF block.
    private static int _webViewFailStreak;
    private static DateTime _webViewCooldownUntilUtc = DateTime.MinValue;

    /// <summary>Reset fail streak when user completes CF so WebView-first can work again.</summary>
    public void ResetWebViewFailStreak()
    {
        Interlocked.Exchange(ref _webViewFailStreak, 0);
        _webViewCooldownUntilUtc = DateTime.MinValue;
    }

    private async Task<byte[]> PerformAsync(
        Uri url,
        string method,
        byte[]? body = null,
        string? contentType = null,
        bool needsCsrf = false,
        bool acceptJson = true,
        bool preferWebView = false)
    {
        bool allowFallback;
        bool forceWebViewFirst;
        lock (_gate)
        {
            allowFallback = _allowWebViewFallback;
            forceWebViewFirst = DateTime.UtcNow < _webViewFirstUntilUtc;
        }

        // While challenge dialog is open (shared WebView is interactive), never use WebView fallback.
        var challengeOpen = SiteAccessStore.Current.NeedsChallenge ||
                            WebViewAPIClient.Shared.IsChallengeUiActive;
        if (challengeOpen)
        {
            allowFallback = false;
            forceWebViewFirst = false;
        }

        // After CF: prefer shared WebView for GETs (HttpClient keeps re-tripping even with clearance).
        var webViewFirst = allowFallback &&
                           method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                           DateTime.UtcNow >= _webViewCooldownUntilUtc &&
                           (preferWebView || forceWebViewFirst);

        if (webViewFirst)
        {
            try
            {
                AppLog.Network($"WebView-first {method} {url}");
                var data = await PerformViaWebViewAsync(url, method, body, contentType, needsCsrf);
                Interlocked.Exchange(ref _webViewFailStreak, 0);
                return data;
            }
            catch (Exception ex)
            {
                AppLog.Warning("network", $"WebView-first failed; trying HttpClient: {ex.Message}");
                NoteWebViewFailure();
            }
        }

        APIError? httpError = null;
        try
        {
            var data = await PerformViaHttpAsync(url, method, body, contentType, needsCsrf, acceptJson);
            // Successful HttpClient read — clear WebView fail streak so future CF can use WebView again.
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref _webViewFailStreak, 0);
            return data;
        }
        catch (APIError error) when (IsTransientNetwork(error))
        {
            // Brief soft retry for connection resets / disposed client / DNS blips.
            AppLog.Warning("network", $"HttpClient transient ({error.Message}); soft retry");
            await Task.Delay(350);
            try
            {
                // Recreate client if a concurrent ResetHttpClient disposed it mid-flight.
                lock (_gate)
                {
                    try { _ = _http.Timeout; }
                    catch
                    {
                        _http = CookieSessionBridge.CreateHttpClient();
                    }
                }
                return await PerformViaHttpAsync(url, method, body, contentType, needsCsrf, acceptJson);
            }
            catch (APIError retryError)
            {
                httpError = retryError;
            }
        }
        catch (APIError error)
        {
            httpError = error;
        }

        if (httpError is null)
            throw new APIError.Network(new Exception("request failed"));

        // Plain Forbidden (e.g. /new.json while guest) is NOT Cloudflare — do not WebView thrash.
        if (httpError is APIError.Forbidden && !httpError.IsChallengeRelated)
            throw MapUserFacing(httpError);

        var shouldFallback = allowFallback &&
                             DateTime.UtcNow >= _webViewCooldownUntilUtc &&
                             (httpError.IsChallengeRelated || IsHardChallenge(httpError));

        if (!shouldFallback)
        {
            // One CF signal is enough — open challenge sheet via PostIfChallenge callers.
            if (httpError.IsChallengeRelated || IsHardChallenge(httpError))
                throw httpError is APIError.CloudflareChallenge
                    ? httpError
                    : new APIError.CloudflareChallenge();
            throw MapUserFacing(httpError);
        }

        AppLog.Warning("network", $"HttpClient blocked ({httpError.Message}); sync cookies + retry");
        await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(CurrentBaseUrl, force: true);

        if (!webViewFirst)
        {
            try
            {
                return await PerformViaHttpAsync(url, method, body, contentType, needsCsrf, acceptJson);
            }
            catch (Exception httpRetryEx)
            {
                AppLog.Warning("network", "HttpClient retry still blocked; WebView fallback: " + httpRetryEx.Message);
            }
        }

        // After CF, avoid WebView fallback stampede: one CF signal is enough to open the sheet.
        // Multiple concurrent WebView GETs abort each other (status=0) and look like "lost clearance".
        if (_webViewFailStreak >= 1 || DateTime.UtcNow < _webViewCooldownUntilUtc)
        {
            NoteWebViewFailure();
            AppLog.Warning("network", "Skip WebView fallback (cooldown/streak) — surface challenge once");
            throw new APIError.CloudflareChallenge();
        }

        try
        {
            // Never EnsureWarm navigate here — that reloads home and drops mid-session CF state.
            // Fetch path warms only if document is blank.
            var data = await PerformViaWebViewAsync(url, method, body, contentType, needsCsrf);
            Interlocked.Exchange(ref _webViewFailStreak, 0);
            return data;
        }
        catch (APIError webError)
        {
            NoteWebViewFailure();
            if (webError.IsChallengeRelated || IsHardChallenge(webError) || IsTransientNetwork(webError))
                throw new APIError.CloudflareChallenge();
            throw MapUserFacing(webError);
        }
        catch
        {
            NoteWebViewFailure();
            throw new APIError.CloudflareChallenge();
        }
    }

    private static void NoteWebViewFailure()
    {
        var n = Interlocked.Increment(ref _webViewFailStreak);
        if (n >= 2)
        {
            _webViewCooldownUntilUtc = DateTime.UtcNow.AddSeconds(20);
            AppLog.Warning("network", $"WebView fail streak={n}, cooldown 20s");
        }
    }

    /// <summary>
    /// Only true CF / non-JSON challenge pages. Plain Forbidden (auth/permission) is NOT a challenge.
    /// </summary>
    private static bool IsHardChallenge(APIError error) => error switch
    {
        APIError.CloudflareChallenge => true,
        APIError.NonJsonResponse => true,
        APIError.Http { Status: 503 } => true,
        APIError.Http h when h.Status == 403 && h.IsChallengeRelated => true,
        _ => false
    };

    private static bool IsTransientNetwork(APIError error) => error switch
    {
        APIError.Network => true,
        APIError.Http { Status: 0 } => true,
        _ => false
    };

    private static APIError MapUserFacing(APIError error)
    {
        if (error is APIError.Http { Status: 0 })
            return new APIError.Network(new Exception("网络请求中断，请稍后重试"));
        if (error is APIError.Network { Message: var m } &&
            (m.Contains("HTTP 0", StringComparison.OrdinalIgnoreCase) ||
             m.Contains("status=0", StringComparison.OrdinalIgnoreCase)))
            return new APIError.Network(new Exception("网络请求中断，请稍后重试"));
        return error;
    }

    private async Task<byte[]> PerformViaHttpAsync(
        Uri url, string method, byte[]? body, string? contentType, bool needsCsrf, bool acceptJson)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null)
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        await ApplyHeadersAsync(request, needsCsrf, acceptJson);

        AppLog.Network($"HttpClient {method} {url}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new APIError.Network(ex);
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        var status = (int)response.StatusCode;

        if (response.Headers.TryGetValues("X-CSRF-Token", out var csrfVals))
        {
            var t = csrfVals.FirstOrDefault();
            if (!string.IsNullOrEmpty(t))
            {
                lock (_gate) _csrfToken = t;
            }
        }

        if (ResponseInspector.LooksLikeCloudflare(data, status))
            throw new APIError.CloudflareChallenge();

        if (acceptJson && ResponseInspector.LooksLikeHtml(data) && !ResponseInspector.IsProbablyJson(data))
        {
            if (ResponseInspector.LooksLikeCloudflare(data, status))
                throw new APIError.CloudflareChallenge();
            throw new APIError.NonJsonResponse();
        }

        switch (status)
        {
            case >= 200 and <= 299:
                return data;
            case 401:
                throw new APIError.Unauthorized();
            case 403:
                if (!needsCsrf)
                {
                    if (ResponseInspector.LooksLikeCloudflare(data, 403) || ResponseInspector.LooksLikeHtml(data))
                        throw new APIError.CloudflareChallenge();
                    throw new APIError.Forbidden();
                }
                // CSRF retry for writes — also surface Discourse error body when present.
                {
                    var msg = ExtractErrorMessage(data);
                    string? apiKey;
                    lock (_gate) apiKey = _apiKey;
                    if (apiKey is null)
                    {
                        lock (_gate) _csrfToken = null;
                        await AdoptWebCookiesAsync(force: true, invalidateCsrf: false);
                        var token = await EnsureCsrfTokenAsync(forceRefresh: true);
                        using var retry = new HttpRequestMessage(new HttpMethod(method), url);
                        if (body is not null)
                        {
                            retry.Content = new ByteArrayContent(body);
                            if (contentType is not null)
                                retry.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                        }
                        await ApplyHeadersAsync(retry, needsCsrf: true, acceptJson);
                        try { retry.Headers.Remove("X-CSRF-Token"); } catch { /* ignore */ }
                        retry.Headers.TryAddWithoutValidation("X-CSRF-Token", token);
                        var retryResp = await _http.SendAsync(retry);
                        var retryData = await retryResp.Content.ReadAsByteArrayAsync();
                        var retryStatus = (int)retryResp.StatusCode;
                        if (ResponseInspector.LooksLikeCloudflare(retryData, retryStatus))
                            throw new APIError.CloudflareChallenge();
                        if (retryStatus is >= 200 and <= 299) return retryData;
                        if (retryStatus == 401) throw new APIError.Unauthorized();
                        if (retryStatus == 403)
                        {
                            lock (_gate) _csrfToken = null;
                            var retryMsg = ExtractErrorMessage(retryData);
                            // Prefer server message over generic Forbidden so UI can show CSRF/login hints.
                            if (!string.IsNullOrWhiteSpace(retryMsg) &&
                                !retryMsg.StartsWith('<') &&
                                retryMsg.Length < 400)
                                throw new APIError.Http(403, retryMsg);
                            throw new APIError.Forbidden();
                        }
                        throw new APIError.Http(retryStatus, ExtractErrorMessage(retryData));
                    }
                    lock (_gate) _csrfToken = null;
                    if (!string.IsNullOrWhiteSpace(msg) && !msg.StartsWith('<') && msg.Length < 400)
                        throw new APIError.Http(403, msg);
                    throw new APIError.Forbidden();
                }
            case 422:
                // Discourse validation / bad payload — keep the server errors array.
                throw new APIError.Http(422, ExtractErrorMessage(data));
            case 429:
                throw new APIError.RateLimited();
            case 503:
                if (ResponseInspector.LooksLikeCloudflare(data, 503))
                    throw new APIError.CloudflareChallenge();
                throw new APIError.Http(503, ExtractErrorMessage(data));
            default:
                throw new APIError.Http(status, ExtractErrorMessage(data));
        }
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage request, bool needsCsrf, bool acceptJson)
    {
        string? apiKey, clientId, csrf;
        Uri baseUrl;
        lock (_gate)
        {
            apiKey = _apiKey;
            clientId = _clientId;
            csrf = _csrfToken;
            baseUrl = _baseUrl;
        }

        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("User-Api-Key", apiKey);
        if (clientId is not null)
            request.Headers.TryAddWithoutValidation("User-Api-Client-Id", clientId);

        // Full browser-like surface (UA + client hints + Sec-Fetch-*) so CF sees XHR, not a bare bot.
        CookieSessionBridge.ApplyBrowserHeaders(
            request,
            pageUrl: baseUrl,
            acceptJson: acceptJson,
            fetchSite: "same-origin",
            fetchMode: "cors",
            fetchDest: "empty");

        request.Headers.TryAddWithoutValidation("Discourse-Present", "true");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        // Match Chrome fetch cache behavior for API GETs
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        if (needsCsrf && apiKey is null)
        {
            var token = await EnsureCsrfTokenAsync();
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", token);
        }
        else if (apiKey is null && !string.IsNullOrEmpty(csrf))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        }
    }

    private async Task<byte[]> PerformViaWebViewAsync(
        Uri url, string method, byte[]? body, string? contentType, bool needsCsrf)
    {
        var origin = CurrentBaseUrl.AbsoluteUri.TrimEnd('/');
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = CookieSessionBridge.AcceptJson,
            ["Accept-Language"] = CookieSessionBridge.AcceptLanguage,
            ["User-Agent"] = CookieSessionBridge.UserAgent,
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Discourse-Present"] = "true",
            ["Referer"] = origin + "/",
            ["Origin"] = origin,
            ["sec-ch-ua"] = CookieSessionBridge.SecChUa,
            ["sec-ch-ua-mobile"] = CookieSessionBridge.SecChUaMobile,
            ["sec-ch-ua-platform"] = CookieSessionBridge.SecChUaPlatform,
            ["Sec-Fetch-Site"] = "same-origin",
            ["Sec-Fetch-Mode"] = "cors",
            ["Sec-Fetch-Dest"] = "empty",
            ["Cache-Control"] = "no-cache",
            ["Pragma"] = "no-cache"
        };
        if (contentType is not null) headers["Content-Type"] = contentType;

        string? apiKey, clientId, csrf;
        lock (_gate)
        {
            apiKey = _apiKey;
            clientId = _clientId;
            csrf = _csrfToken;
        }
        if (apiKey is not null) headers["User-Api-Key"] = apiKey;
        if (clientId is not null) headers["User-Api-Client-Id"] = clientId;
        if (needsCsrf && apiKey is null)
            headers["X-CSRF-Token"] = await EnsureCsrfTokenAsync();
        else if (apiKey is null && !string.IsNullOrEmpty(csrf))
            headers["X-CSRF-Token"] = csrf!;

        string? bodyJson = body is null ? null : Encoding.UTF8.GetString(body);
        AppLog.Network($"WebView {method} {url}");

        try
        {
            var data = await WebViewAPIClient.Shared.FetchAsync(url, method, headers, bodyJson);
            var token = ParseCsrfJson(data);
            if (token is not null)
            {
                lock (_gate) _csrfToken = token;
            }
            return data;
        }
        catch (APIError.Forbidden) when (needsCsrf)
        {
            lock (_gate) _csrfToken = null;
            var token = await EnsureCsrfTokenAsync();
            headers["X-CSRF-Token"] = token;
            return await WebViewAPIClient.Shared.FetchAsync(url, method, headers, bodyJson);
        }
    }

    private static string? ExtractErrorMessage(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var msgs = errors.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0);
                var joined = string.Join("；", msgs);
                if (!string.IsNullOrEmpty(joined)) return joined;
            }
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
            if (doc.RootElement.TryGetProperty("error_type", out var et)) return et.GetString();
        }
        catch { /* ignore */ }
        return Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 300)));
    }
}
