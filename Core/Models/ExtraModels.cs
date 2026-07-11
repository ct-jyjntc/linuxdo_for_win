using System.Text.Json;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Models;

public sealed class PollOption
{
    public string Id { get; set; } = "";
    public string? Html { get; set; }
    public int Votes { get; set; }
    public string PlainLabel => string.IsNullOrEmpty(Html) ? Id : HtmlText.PlainText(Html);

    public static PollOption FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetString) ?? "",
        Html = JsonFlexible.Prop(el, "html", JsonFlexible.GetString),
        Votes = JsonFlexible.Prop(el, "votes", JsonFlexible.GetInt) ?? 0
    };
}

public sealed class DiscoursePoll
{
    public int? ServerId { get; set; }
    public string Name { get; set; } = "poll";
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Results { get; set; }
    public List<PollOption> Options { get; set; } = [];
    public int Voters { get; set; }

    public string Id => $"{ServerId ?? 0}-{Name}";
    public bool IsOpen => (Status ?? "open") == "open";
    public bool IsMultiple => (Type ?? "regular") == "multiple";
    public bool CanSeeResults
    {
        get
        {
            var r = Results ?? "always";
            return r is "always" or "on_vote" || !IsOpen;
        }
    }

    public static DiscoursePoll FromJson(JsonElement el)
    {
        var poll = new DiscoursePoll
        {
            ServerId = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString) ?? "poll",
            Type = JsonFlexible.Prop(el, "type", JsonFlexible.GetString),
            Status = JsonFlexible.Prop(el, "status", JsonFlexible.GetString),
            Results = JsonFlexible.Prop(el, "results", JsonFlexible.GetString),
            Voters = JsonFlexible.Prop(el, "voters", JsonFlexible.GetInt) ?? 0
        };
        if (el.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            poll.Options = JsonFlexible.DecodeLossyArray(opts, e => PollOption.FromJson(e));
        return poll;
    }
}

public static class CommonReactions
{
    public static readonly string[] All =
        ["+1", "heart", "laughing", "open_mouth", "clap", "confetti_ball", "hugs", "heart_eyes"];

    public static string Display(string id) => id switch
    {
        "+1" => "👍",
        "heart" => "❤️",
        "laughing" => "😄",
        "open_mouth" => "😮",
        "clap" => "👏",
        "confetti_ball" => "🎉",
        "hugs" => "🤗",
        "heart_eyes" => "😍",
        _ => id
    };
}

public enum PostFlagType
{
    OffTopic = 3,
    Inappropriate = 4,
    Spam = 8,
    NotifyUser = 6,
    NotifyModerators = 7
}

public static class PostFlagTypeExtensions
{
    public static string Title(this PostFlagType t) => t switch
    {
        PostFlagType.OffTopic => "跑题",
        PostFlagType.Inappropriate => "不当内容",
        PostFlagType.Spam => "垃圾信息",
        PostFlagType.NotifyUser => "提醒用户",
        PostFlagType.NotifyModerators => "通知版主",
        _ => "举报"
    };
}

public sealed class UserSearchItem
{
    public int? Id { get; set; }
    public string Username { get; set; } = "";
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }

    public static UserSearchItem FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString) ?? "",
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString)
    };
}

public sealed class NestedPostNode
{
    public int PostNumber { get; set; }
    public int? PostId { get; set; }
    public string? Username { get; set; }
    public string Excerpt { get; set; } = "";
    public int ReplyCount { get; set; }
    public string Id => $"{PostNumber}-{PostId ?? 0}";
}

public sealed class FollowUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "user";
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name! : Username;
    public Uri? AvatarUrl(Uri baseUrl, int size = 96)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public static FollowUser FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString) ?? "user",
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString)
    };
}

public sealed class ReactionUsersGroup
{
    public string Reaction { get; set; } = "";
    public int Count { get; set; }
    public List<FollowUser> Users { get; set; } = [];
    public string Id => Reaction;

    public static ReactionUsersGroup FromJson(JsonElement el)
    {
        var g = new ReactionUsersGroup
        {
            Reaction = JsonFlexible.Prop(el, "id", JsonFlexible.GetString) ?? "",
            Count = JsonFlexible.Prop(el, "count", JsonFlexible.GetInt) ?? 0
        };
        if (el.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            g.Users = JsonFlexible.DecodeLossyArray(users, e => FollowUser.FromJson(e));
        return g;
    }
}

public sealed class PostBoost
{
    public int Id { get; set; }
    public string? Cooked { get; set; }
    public string? Raw { get; set; }
    public bool? CanDelete { get; set; }
    public BoostUser? User { get; set; }
    public string BodyText =>
        !string.IsNullOrEmpty(Cooked) ? HtmlText.PlainText(Cooked) : (Raw ?? "");

    public static PostBoost FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Cooked = JsonFlexible.Prop(el, "cooked", JsonFlexible.GetString),
        Raw = JsonFlexible.Prop(el, "raw", JsonFlexible.GetString),
        CanDelete = JsonFlexible.Prop(el, "can_delete", JsonFlexible.GetBool),
        User = el.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            ? BoostUser.FromJson(u) : null
    };
}

public sealed class BoostUser
{
    public int? Id { get; set; }
    public string? Username { get; set; }
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }

    public static BoostUser FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString),
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString)
    };
}

public enum UserNotificationLevel
{
    Normal,
    Mute,
    Ignore
}

public static class UserNotificationLevelExtensions
{
    public static string ApiValue(this UserNotificationLevel level) => level switch
    {
        UserNotificationLevel.Mute => "mute",
        UserNotificationLevel.Ignore => "ignore",
        _ => "normal"
    };

    public static string Title(this UserNotificationLevel level) => level switch
    {
        UserNotificationLevel.Mute => "静音",
        UserNotificationLevel.Ignore => "忽略",
        _ => "正常"
    };
}

// ── Server drafts ─────────────────────────────────────────

public sealed class ServerDraftPayload
{
    public string? Reply { get; set; }
    public string? Title { get; set; }
    public int? CategoryId { get; set; }
    public List<string>? Tags { get; set; }
    public int? ReplyToPostNumber { get; set; }
    public string? Action { get; set; }
    public List<string>? Recipients { get; set; }
    public string? ArchetypeId { get; set; }

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Reply) || !string.IsNullOrWhiteSpace(Title);

    public string ToJsonString()
    {
        var dict = new Dictionary<string, object?>();
        if (Reply is not null) dict["reply"] = Reply;
        if (Title is not null) dict["title"] = Title;
        if (CategoryId is not null) dict["categoryId"] = CategoryId;
        if (Tags is not null) dict["tags"] = Tags;
        if (ReplyToPostNumber is not null) dict["replyToPostNumber"] = ReplyToPostNumber;
        if (Action is not null) dict["action"] = Action;
        if (Recipients is not null) dict["recipients"] = Recipients;
        if (ArchetypeId is not null) dict["archetypeId"] = ArchetypeId;
        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    public static ServerDraftPayload? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return FromJson(doc.RootElement);
        }
        catch { return null; }
    }

    public static ServerDraftPayload FromJson(JsonElement el) => new()
    {
        Reply = JsonFlexible.Prop(el, "reply", JsonFlexible.GetString),
        Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
        CategoryId = JsonFlexible.Prop(el, "categoryId", JsonFlexible.GetInt)
                     ?? JsonFlexible.Prop(el, "category_id", JsonFlexible.GetInt),
        Tags = el.TryGetProperty("tags", out var tags) ? JsonFlexible.GetStringArray(tags) : null,
        ReplyToPostNumber = JsonFlexible.Prop(el, "replyToPostNumber", JsonFlexible.GetInt)
                            ?? JsonFlexible.Prop(el, "reply_to_post_number", JsonFlexible.GetInt),
        Action = JsonFlexible.Prop(el, "action", JsonFlexible.GetString),
        Recipients = el.TryGetProperty("recipients", out var rec) ? JsonFlexible.GetStringArray(rec) : null,
        ArchetypeId = JsonFlexible.Prop(el, "archetypeId", JsonFlexible.GetString)
                      ?? JsonFlexible.Prop(el, "archetype_id", JsonFlexible.GetString)
    };
}

public sealed class ServerDraft
{
    public string DraftKey { get; set; } = "";
    public int Sequence { get; set; }
    public ServerDraftPayload? Data { get; set; }
    public string? CreatedAt { get; set; }
    public string? DraftUsername { get; set; }

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrEmpty(Data?.Title)) return Data!.Title!;
            if (DraftKey.StartsWith("topic_", StringComparison.Ordinal)) return "回复草稿";
            if (DraftKey.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                Data?.ArchetypeId == "private_message") return "私信草稿";
            return "新主题草稿";
        }
    }

    public string Excerpt => Data?.Reply ?? "";
    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public static ServerDraft FromJson(JsonElement el)
    {
        ServerDraftPayload? payload = null;
        if (el.TryGetProperty("data", out var dataEl))
        {
            if (dataEl.ValueKind == JsonValueKind.Object)
                payload = ServerDraftPayload.FromJson(dataEl);
            else if (dataEl.ValueKind == JsonValueKind.String)
                payload = ServerDraftPayload.Parse(dataEl.GetString());
        }

        return new ServerDraft
        {
            DraftKey = JsonFlexible.Prop(el, "draft_key", JsonFlexible.GetString) ?? Guid.NewGuid().ToString(),
            Sequence = JsonFlexible.Prop(el, "sequence", JsonFlexible.GetInt)
                       ?? JsonFlexible.Prop(el, "draft_sequence", JsonFlexible.GetInt)
                       ?? 0,
            Data = payload,
            CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
            DraftUsername = JsonFlexible.Prop(el, "draft_username", JsonFlexible.GetString)
        };
    }
}

public static class DiscourseDraftKey
{
    public const string NewTopic = "new_topic";
    public static string Topic(int id) => $"topic_{id}";
    public static string Topic(int id, int postNumber) => $"topic_{id}_{postNumber}";
    public static string PrivateMessage() => "new_private_message";
}

// ── Invites ───────────────────────────────────────────────

public sealed class InviteLink
{
    public int Id { get; set; }
    public string Link { get; set; } = "";
    public int? MaxRedemptions { get; set; }
    public int? RedemptionCount { get; set; }
    public bool? Expired { get; set; }
    public string? ExpiresAt { get; set; }
    public string? CreatedAt { get; set; }
    public string? Description { get; set; }

    public string RemainingText
    {
        get
        {
            var used = RedemptionCount ?? 0;
            return MaxRedemptions is int max ? $"{used}/{max} 次" : $"已用 {used} 次";
        }
    }

    public DateTimeOffset? ExpiresDate => DiscourseDateParser.Parse(ExpiresAt);
    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public static InviteLink FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Link = JsonFlexible.Prop(el, "invite_link", JsonFlexible.GetString)
               ?? JsonFlexible.Prop(el, "url", JsonFlexible.GetString)
               ?? JsonFlexible.Prop(el, "link", JsonFlexible.GetString)
               ?? "",
        MaxRedemptions = JsonFlexible.Prop(el, "max_redemptions_allowed", JsonFlexible.GetInt),
        RedemptionCount = JsonFlexible.Prop(el, "redemption_count", JsonFlexible.GetInt),
        Expired = JsonFlexible.Prop(el, "expired", JsonFlexible.GetBool),
        ExpiresAt = JsonFlexible.Prop(el, "expires_at", JsonFlexible.GetString),
        CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
        Description = JsonFlexible.Prop(el, "description", JsonFlexible.GetString)
    };
}

// ── User summary / activity ───────────────────────────────

public sealed class UserSummary
{
    public int? LikesGiven { get; set; }
    public int? LikesReceived { get; set; }
    public int? TopicsEntered { get; set; }
    public int? PostsReadCount { get; set; }
    public int? DaysVisited { get; set; }
    public int? TopicCount { get; set; }
    public int? PostCount { get; set; }
    public int? TimeRead { get; set; }
    public int? BookmarkCount { get; set; }
    public int? SolvedCount { get; set; }

    public static UserSummary FromJson(JsonElement el) => new()
    {
        LikesGiven = JsonFlexible.Prop(el, "likes_given", JsonFlexible.GetInt),
        LikesReceived = JsonFlexible.Prop(el, "likes_received", JsonFlexible.GetInt),
        TopicsEntered = JsonFlexible.Prop(el, "topics_entered", JsonFlexible.GetInt),
        PostsReadCount = JsonFlexible.Prop(el, "posts_read_count", JsonFlexible.GetInt),
        DaysVisited = JsonFlexible.Prop(el, "days_visited", JsonFlexible.GetInt),
        TopicCount = JsonFlexible.Prop(el, "topic_count", JsonFlexible.GetInt),
        PostCount = JsonFlexible.Prop(el, "post_count", JsonFlexible.GetInt),
        TimeRead = JsonFlexible.Prop(el, "time_read", JsonFlexible.GetInt),
        BookmarkCount = JsonFlexible.Prop(el, "bookmark_count", JsonFlexible.GetInt),
        SolvedCount = JsonFlexible.Prop(el, "solved_count", JsonFlexible.GetInt)
    };
}

public sealed class UserSummaryResponse
{
    public UserSummary? Summary { get; set; }
    public List<UserBadge> Badges { get; set; } = [];
}

public sealed class UserBadge
{
    public int? BadgeId { get; set; }
    public string Name { get; set; } = "徽章";
    public string? Description { get; set; }
    public int? GrantCount { get; set; }

    public static UserBadge FromJson(JsonElement el) => new()
    {
        BadgeId = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString) ?? "徽章",
        Description = JsonFlexible.Prop(el, "description", JsonFlexible.GetString),
        GrantCount = JsonFlexible.Prop(el, "grant_count", JsonFlexible.GetInt)
    };
}

public enum UserActionFilter
{
    All = 0,
    LikesGiven = 1,
    LikesReceived = 2,
    Bookmarks = 3,
    Topics = 4,
    Replies = 5,
    Mentions = 7
}

public sealed class UserAction
{
    public int? ActionType { get; set; }
    public string? CreatedAt { get; set; }
    public string? Excerpt { get; set; }
    public string? Title { get; set; }
    public int? TopicId { get; set; }
    public int? PostNumber { get; set; }
    public string? Username { get; set; }

    public string DisplayTitle =>
        !string.IsNullOrEmpty(Title) ? Title! :
        !string.IsNullOrEmpty(Excerpt) ? HtmlText.PlainText(Excerpt) : "动态";

    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public static UserAction FromJson(JsonElement el) => new()
    {
        ActionType = JsonFlexible.Prop(el, "action_type", JsonFlexible.GetInt),
        CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
        Excerpt = JsonFlexible.Prop(el, "excerpt", JsonFlexible.GetString),
        Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
        TopicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt),
        PostNumber = JsonFlexible.Prop(el, "post_number", JsonFlexible.GetInt),
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString)
    };
}

public sealed class PostTemplate
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Raw { get; set; }
    public string Body => Raw ?? Content ?? "";
    public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title! : $"模板 {Id}";

    public static PostTemplate FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
        Content = JsonFlexible.Prop(el, "content", JsonFlexible.GetString),
        Raw = JsonFlexible.Prop(el, "raw", JsonFlexible.GetString)
    };
}
