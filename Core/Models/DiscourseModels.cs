using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Models;

public sealed class DiscourseUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "user";
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }
    public int? TrustLevel { get; set; }
    public string? Title { get; set; }
    public bool? Moderator { get; set; }
    public bool? Admin { get; set; }

    public Uri? AvatarUrl(Uri baseUrl, int size = 120)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public static DiscourseUser? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return new DiscourseUser
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString) ?? "user",
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
            AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
            TrustLevel = JsonFlexible.Prop(el, "trust_level", JsonFlexible.GetInt),
            Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
            Moderator = JsonFlexible.Prop(el, "moderator", JsonFlexible.GetBool),
            Admin = JsonFlexible.Prop(el, "admin", JsonFlexible.GetBool)
        };
    }
}

public static class AvatarUrlHelper
{
    public static Uri? Make(string? template, Uri baseUrl, int size)
    {
        if (string.IsNullOrEmpty(template)) return null;
        var snapped = size switch
        {
            <= 48 => 45,
            <= 72 => 60,
            <= 100 => 90,
            <= 160 => 120,
            <= 280 => 240,
            _ => 360
        };
        var path = template.Replace("{size}", snapped.ToString(), StringComparison.Ordinal);
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(path, UriKind.Absolute, out var abs) ? abs : null;
        if (path.StartsWith("//", StringComparison.Ordinal))
            return Uri.TryCreate("https:" + path, UriKind.Absolute, out var proto) ? proto : null;
        try { return new Uri(baseUrl, path); }
        catch { return null; }
    }
}

public sealed class DiscourseCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? TextColor { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public int? TopicCount { get; set; }
    public int? PostCount { get; set; }
    public int? ParentCategoryId { get; set; }
    public List<int>? SubcategoryIds { get; set; }
    public bool? ReadRestricted { get; set; }

    public static DiscourseCategory? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return new DiscourseCategory
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString) ?? "",
            Color = JsonFlexible.Prop(el, "color", JsonFlexible.GetString),
            TextColor = JsonFlexible.Prop(el, "text_color", JsonFlexible.GetString),
            Slug = JsonFlexible.Prop(el, "slug", JsonFlexible.GetString),
            Description = JsonFlexible.Prop(el, "description", JsonFlexible.GetString),
            TopicCount = JsonFlexible.Prop(el, "topic_count", JsonFlexible.GetInt),
            PostCount = JsonFlexible.Prop(el, "post_count", JsonFlexible.GetInt),
            ParentCategoryId = JsonFlexible.Prop(el, "parent_category_id", JsonFlexible.GetInt),
            SubcategoryIds = el.TryGetProperty("subcategory_ids", out var ids) ? JsonFlexible.GetIntArray(ids) : null,
            ReadRestricted = JsonFlexible.Prop(el, "read_restricted", JsonFlexible.GetBool)
        };
    }
}

public sealed class TopicPoster
{
    public string? Extras { get; set; }
    public string? Description { get; set; }
    public int? UserId { get; set; }
    public int? PrimaryGroupId { get; set; }

    public static TopicPoster FromJson(JsonElement el) => new()
    {
        Extras = JsonFlexible.Prop(el, "extras", JsonFlexible.GetString),
        Description = JsonFlexible.Prop(el, "description", JsonFlexible.GetString),
        UserId = JsonFlexible.Prop(el, "user_id", JsonFlexible.GetInt),
        PrimaryGroupId = JsonFlexible.Prop(el, "primary_group_id", JsonFlexible.GetInt)
    };
}

public sealed class DiscourseTopic
{
    public int Id { get; set; }
    public string Title { get; set; } = "(无标题)";
    public string? FancyTitle { get; set; }
    public string? Slug { get; set; }
    public int? PostsCount { get; set; }
    public int? ReplyCount { get; set; }
    public int? HighestPostNumber { get; set; }
    public string? ImageUrl { get; set; }
    public string? CreatedAt { get; set; }
    public string? LastPostedAt { get; set; }
    public bool? Bumped { get; set; }
    public string? BumpedAt { get; set; }
    public string? Archetype { get; set; }
    public bool? Unseen { get; set; }
    public bool? Pinned { get; set; }
    public bool? Visible { get; set; }
    public bool? Closed { get; set; }
    public bool? Archived { get; set; }
    public bool Bookmarked { get; set; }
    public bool Liked { get; set; }
    public List<string>? Tags { get; set; }
    public int? CategoryId { get; set; }
    public List<TopicPoster>? Posters { get; set; }
    public int? Views { get; set; }
    public int? LikeCount { get; set; }
    public bool? HasSummary { get; set; }
    public string? Excerpt { get; set; }

    public string DisplayTitle =>
        !string.IsNullOrEmpty(FancyTitle) ? FancyTitle! : Title;

    public bool IsPinned => Pinned == true;
    public bool IsClosed => Closed == true;

    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);
    public DateTimeOffset? LastPostedDate => DiscourseDateParser.Parse(LastPostedAt);
    public DateTimeOffset? BumpedDate => DiscourseDateParser.Parse(BumpedAt);

    public static DiscourseTopic? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        List<string>? tags = null;
        if (el.TryGetProperty("tags", out var tagsEl))
            tags = JsonFlexible.GetStringArray(tagsEl);

        List<TopicPoster>? posters = null;
        if (el.TryGetProperty("posters", out var postersEl) && postersEl.ValueKind == JsonValueKind.Array)
            posters = JsonFlexible.DecodeLossyArray(postersEl, e => TopicPoster.FromJson(e));

        return new DiscourseTopic
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString) ?? "(无标题)",
            FancyTitle = JsonFlexible.Prop(el, "fancy_title", JsonFlexible.GetString),
            Slug = JsonFlexible.Prop(el, "slug", JsonFlexible.GetString),
            PostsCount = JsonFlexible.Prop(el, "posts_count", JsonFlexible.GetInt),
            ReplyCount = JsonFlexible.Prop(el, "reply_count", JsonFlexible.GetInt),
            HighestPostNumber = JsonFlexible.Prop(el, "highest_post_number", JsonFlexible.GetInt),
            ImageUrl = JsonFlexible.Prop(el, "image_url", JsonFlexible.GetString),
            CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
            LastPostedAt = JsonFlexible.Prop(el, "last_posted_at", JsonFlexible.GetString),
            Bumped = JsonFlexible.Prop(el, "bumped", JsonFlexible.GetBool),
            BumpedAt = JsonFlexible.Prop(el, "bumped_at", JsonFlexible.GetString),
            Archetype = JsonFlexible.Prop(el, "archetype", JsonFlexible.GetString),
            Unseen = JsonFlexible.Prop(el, "unseen", JsonFlexible.GetBool),
            Pinned = JsonFlexible.Prop(el, "pinned", JsonFlexible.GetBool),
            Visible = JsonFlexible.Prop(el, "visible", JsonFlexible.GetBool),
            Closed = JsonFlexible.Prop(el, "closed", JsonFlexible.GetBool),
            Archived = JsonFlexible.Prop(el, "archived", JsonFlexible.GetBool),
            Bookmarked = JsonFlexible.Prop(el, "bookmarked", JsonFlexible.GetBool) ?? false,
            Liked = JsonFlexible.Prop(el, "liked", JsonFlexible.GetBool) ?? false,
            Tags = tags,
            CategoryId = JsonFlexible.Prop(el, "category_id", JsonFlexible.GetInt),
            Posters = posters,
            Views = JsonFlexible.Prop(el, "views", JsonFlexible.GetInt),
            LikeCount = JsonFlexible.Prop(el, "like_count", JsonFlexible.GetInt),
            HasSummary = JsonFlexible.Prop(el, "has_summary", JsonFlexible.GetBool),
            Excerpt = JsonFlexible.Prop(el, "excerpt", JsonFlexible.GetString)
        };
    }
}

public sealed class PostActionSummary
{
    public int Id { get; set; }
    public int? Count { get; set; }
    public bool? Hidden { get; set; }
    public bool? CanAct { get; set; }
    public bool? CanUndo { get; set; }
    public bool? Acted { get; set; }

    public static PostActionSummary FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Count = JsonFlexible.Prop(el, "count", JsonFlexible.GetInt),
        Hidden = JsonFlexible.Prop(el, "hidden", JsonFlexible.GetBool),
        CanAct = JsonFlexible.Prop(el, "can_act", JsonFlexible.GetBool),
        CanUndo = JsonFlexible.Prop(el, "can_undo", JsonFlexible.GetBool),
        Acted = JsonFlexible.Prop(el, "acted", JsonFlexible.GetBool)
    };
}

public sealed class PostReaction
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public int Count { get; set; }

    public static PostReaction FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetString) ?? "",
        Type = JsonFlexible.Prop(el, "type", JsonFlexible.GetString),
        Count = JsonFlexible.Prop(el, "count", JsonFlexible.GetInt) ?? 0
    };
}

public sealed class DiscoursePost
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Username { get; set; }
    public string? AvatarTemplate { get; set; }
    public string? CreatedAt { get; set; }
    public string? Cooked { get; set; }
    public int? PostNumber { get; set; }
    public int? PostType { get; set; }
    public string? UpdatedAt { get; set; }
    public int? ReplyCount { get; set; }
    public int? ReplyToPostNumber { get; set; }
    public bool? Yours { get; set; }
    public int? TopicId { get; set; }
    public string? TopicSlug { get; set; }
    public string? DisplayUsername { get; set; }
    public int? Version { get; set; }
    public bool? CanEdit { get; set; }
    public bool? CanDelete { get; set; }
    public bool? CanRecover { get; set; }
    public string? UserTitle { get; set; }
    public bool? Bookmarked { get; set; }
    public int? BookmarkId { get; set; }
    public string? Raw { get; set; }
    public List<PostActionSummary>? ActionsSummary { get; set; }
    public bool? Moderator { get; set; }
    public bool? Admin { get; set; }
    public bool? Staff { get; set; }
    public int? UserId { get; set; }
    public bool? Hidden { get; set; }
    public int? TrustLevel { get; set; }
    public string? DeletedAt { get; set; }
    public bool? UserDeleted { get; set; }
    public List<PostReaction>? Reactions { get; set; }
    public PostReaction? CurrentUserReaction { get; set; }
    public List<DiscoursePoll>? Polls { get; set; }
    public Dictionary<string, List<string>>? PollsVotes { get; set; }
    public List<PostBoost>? Boosts { get; set; }
    public bool? AcceptedAnswer { get; set; }
    public bool? CanAcceptAnswer { get; set; }

    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public string? AuthorUsername =>
        !string.IsNullOrEmpty(Username) ? Username :
        !string.IsNullOrEmpty(DisplayUsername) ? DisplayUsername : null;

    public int LikeCount => ActionsSummary?.FirstOrDefault(a => a.Id == 2)?.Count ?? 0;
    public bool Liked => ActionsSummary?.FirstOrDefault(a => a.Id == 2)?.Acted ?? false;
    public bool CanLike => ActionsSummary?.FirstOrDefault(a => a.Id == 2)?.CanAct ?? false;

    public Uri? AvatarUrl(Uri baseUrl, int size = 90)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public DiscoursePost WithLikeToggled()
    {
        var copy = Clone();
        var summary = copy.ActionsSummary?.ToList() ?? [];
        var idx = summary.FindIndex(a => a.Id == 2);
        if (idx >= 0)
        {
            var action = summary[idx];
            var wasLiked = action.Acted ?? false;
            action.Acted = !wasLiked;
            action.Count = Math.Max(0, (action.Count ?? 0) + (wasLiked ? -1 : 1));
            summary[idx] = action;
        }
        else
        {
            summary.Add(new PostActionSummary
            {
                Id = 2, Count = 1, Hidden = false, CanAct = true, CanUndo = true, Acted = true
            });
        }
        copy.ActionsSummary = summary;
        return copy;
    }

    public DiscoursePost Clone()
    {
        return new DiscoursePost
        {
            Id = Id,
            Name = Name,
            Username = Username,
            AvatarTemplate = AvatarTemplate,
            CreatedAt = CreatedAt,
            Cooked = Cooked,
            PostNumber = PostNumber,
            PostType = PostType,
            UpdatedAt = UpdatedAt,
            ReplyCount = ReplyCount,
            ReplyToPostNumber = ReplyToPostNumber,
            Yours = Yours,
            TopicId = TopicId,
            TopicSlug = TopicSlug,
            DisplayUsername = DisplayUsername,
            Version = Version,
            CanEdit = CanEdit,
            CanDelete = CanDelete,
            CanRecover = CanRecover,
            UserTitle = UserTitle,
            Bookmarked = Bookmarked,
            BookmarkId = BookmarkId,
            Raw = Raw,
            ActionsSummary = ActionsSummary?.Select(a => new PostActionSummary
            {
                Id = a.Id, Count = a.Count, Hidden = a.Hidden, CanAct = a.CanAct, CanUndo = a.CanUndo, Acted = a.Acted
            }).ToList(),
            Moderator = Moderator,
            Admin = Admin,
            Staff = Staff,
            UserId = UserId,
            Hidden = Hidden,
            TrustLevel = TrustLevel,
            DeletedAt = DeletedAt,
            UserDeleted = UserDeleted,
            Reactions = Reactions?.Select(r => new PostReaction { Id = r.Id, Type = r.Type, Count = r.Count }).ToList(),
            CurrentUserReaction = CurrentUserReaction is null ? null : new PostReaction
            {
                Id = CurrentUserReaction.Id, Type = CurrentUserReaction.Type, Count = CurrentUserReaction.Count
            },
            Polls = Polls,
            PollsVotes = PollsVotes is null ? null : new Dictionary<string, List<string>>(PollsVotes),
            Boosts = Boosts?.ToList(),
            AcceptedAnswer = AcceptedAnswer,
            CanAcceptAnswer = CanAcceptAnswer
        };
    }

    public DiscoursePost WithReactionToggled(string reactionId)
    {
        var copy = Clone();
        var list = copy.Reactions?.ToList() ?? [];
        var currentId = copy.CurrentUserReaction?.Id;
        if (currentId == reactionId)
        {
            var idx = list.FindIndex(r => r.Id == reactionId);
            if (idx >= 0)
            {
                list[idx].Count = Math.Max(0, list[idx].Count - 1);
                if (list[idx].Count == 0) list.RemoveAt(idx);
            }
            copy.CurrentUserReaction = null;
        }
        else
        {
            if (currentId is not null)
            {
                var idx = list.FindIndex(r => r.Id == currentId);
                if (idx >= 0)
                {
                    list[idx].Count = Math.Max(0, list[idx].Count - 1);
                    if (list[idx].Count == 0) list.RemoveAt(idx);
                }
            }
            var existing = list.FindIndex(r => r.Id == reactionId);
            if (existing >= 0) list[existing].Count += 1;
            else list.Add(new PostReaction { Id = reactionId, Count = 1 });
            copy.CurrentUserReaction = new PostReaction { Id = reactionId, Count = 1 };
        }
        copy.Reactions = list;
        return copy;
    }

    public static DiscoursePost? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        List<PostActionSummary>? actions = null;
        if (el.TryGetProperty("actions_summary", out var acts) && acts.ValueKind == JsonValueKind.Array)
            actions = JsonFlexible.DecodeLossyArray(acts, e => PostActionSummary.FromJson(e));

        List<PostReaction>? reactions = null;
        if (el.TryGetProperty("reactions", out var reacts) && reacts.ValueKind == JsonValueKind.Array)
            reactions = JsonFlexible.DecodeLossyArray(reacts, e => PostReaction.FromJson(e));

        PostReaction? current = null;
        if (el.TryGetProperty("current_user_reaction", out var cur) && cur.ValueKind == JsonValueKind.Object)
            current = PostReaction.FromJson(cur);

        var post = new DiscoursePost
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
            Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString),
            AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
            CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
            Cooked = JsonFlexible.Prop(el, "cooked", JsonFlexible.GetString),
            PostNumber = JsonFlexible.Prop(el, "post_number", JsonFlexible.GetInt),
            PostType = JsonFlexible.Prop(el, "post_type", JsonFlexible.GetInt),
            UpdatedAt = JsonFlexible.Prop(el, "updated_at", JsonFlexible.GetString),
            ReplyCount = JsonFlexible.Prop(el, "reply_count", JsonFlexible.GetInt),
            ReplyToPostNumber = JsonFlexible.Prop(el, "reply_to_post_number", JsonFlexible.GetInt),
            Yours = JsonFlexible.Prop(el, "yours", JsonFlexible.GetBool),
            TopicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt),
            TopicSlug = JsonFlexible.Prop(el, "topic_slug", JsonFlexible.GetString),
            DisplayUsername = JsonFlexible.Prop(el, "display_username", JsonFlexible.GetString),
            Version = JsonFlexible.Prop(el, "version", JsonFlexible.GetInt),
            CanEdit = JsonFlexible.Prop(el, "can_edit", JsonFlexible.GetBool),
            CanDelete = JsonFlexible.Prop(el, "can_delete", JsonFlexible.GetBool),
            CanRecover = JsonFlexible.Prop(el, "can_recover", JsonFlexible.GetBool),
            UserTitle = JsonFlexible.Prop(el, "user_title", JsonFlexible.GetString),
            Bookmarked = JsonFlexible.Prop(el, "bookmarked", JsonFlexible.GetBool),
            BookmarkId = JsonFlexible.Prop(el, "bookmark_id", JsonFlexible.GetInt),
            Raw = JsonFlexible.Prop(el, "raw", JsonFlexible.GetString),
            ActionsSummary = actions,
            Moderator = JsonFlexible.Prop(el, "moderator", JsonFlexible.GetBool),
            Admin = JsonFlexible.Prop(el, "admin", JsonFlexible.GetBool),
            Staff = JsonFlexible.Prop(el, "staff", JsonFlexible.GetBool),
            UserId = JsonFlexible.Prop(el, "user_id", JsonFlexible.GetInt),
            Hidden = JsonFlexible.Prop(el, "hidden", JsonFlexible.GetBool),
            TrustLevel = JsonFlexible.Prop(el, "trust_level", JsonFlexible.GetInt),
            DeletedAt = JsonFlexible.Prop(el, "deleted_at", JsonFlexible.GetString),
            UserDeleted = JsonFlexible.Prop(el, "user_deleted", JsonFlexible.GetBool),
            Reactions = reactions,
            CurrentUserReaction = current,
            AcceptedAnswer = JsonFlexible.Prop(el, "accepted_answer", JsonFlexible.GetBool),
            CanAcceptAnswer = JsonFlexible.Prop(el, "can_accept_answer", JsonFlexible.GetBool)
        };

        if (el.TryGetProperty("polls", out var pollsEl) && pollsEl.ValueKind == JsonValueKind.Array)
            post.Polls = JsonFlexible.DecodeLossyArray(pollsEl, e => DiscoursePoll.FromJson(e));

        if (el.TryGetProperty("polls_votes", out var votesEl) && votesEl.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var prop in votesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    map[prop.Name] = prop.Value.EnumerateArray()
                        .Select(v => JsonFlexible.GetString(v) ?? "")
                        .Where(s => s.Length > 0).ToList();
                else if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = [prop.Value.GetString() ?? ""];
            }
            post.PollsVotes = map;
        }

        if (el.TryGetProperty("boosts", out var boostsEl) && boostsEl.ValueKind == JsonValueKind.Array)
            post.Boosts = JsonFlexible.DecodeLossyArray(boostsEl, e => PostBoost.FromJson(e));

        return post;
    }
}

public sealed class TopicListPayload
{
    public bool? CanCreateTopic { get; set; }
    public string? MoreTopicsUrl { get; set; }
    public List<DiscourseTopic> Topics { get; set; } = [];
}

public sealed class TopicListResponse
{
    public List<DiscourseUser>? Users { get; set; }
    public TopicListPayload TopicList { get; set; } = new();

    public static TopicListResponse FromJson(JsonElement root)
    {
        var resp = new TopicListResponse();
        if (root.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            resp.Users = JsonFlexible.DecodeLossyArray(users, DiscourseUser.FromJson);

        if (root.TryGetProperty("topic_list", out var tl) && tl.ValueKind == JsonValueKind.Object)
        {
            resp.TopicList.CanCreateTopic = JsonFlexible.Prop(tl, "can_create_topic", JsonFlexible.GetBool);
            resp.TopicList.MoreTopicsUrl = JsonFlexible.Prop(tl, "more_topics_url", JsonFlexible.GetString);
            if (tl.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array)
                resp.TopicList.Topics = JsonFlexible.DecodeLossyArray(topics, DiscourseTopic.FromJson);
        }
        return resp;
    }
}

public sealed class TopicDetails
{
    public DiscourseUser? CreatedBy { get; set; }
    public DiscourseUser? LastPoster { get; set; }
    public List<DiscourseUser>? Participants { get; set; }
    public bool? CanEdit { get; set; }
    public bool? CanCreatePost { get; set; }
    public bool? CanFlagTopic { get; set; }
    public bool? CanCloseTopic { get; set; }
    public bool? CanArchiveTopic { get; set; }
    public bool? CanPinTopic { get; set; }
    public bool? CanModerateCategory { get; set; }

    public bool CanModerateTopic =>
        CanCloseTopic == true || CanArchiveTopic == true || CanPinTopic == true || CanModerateCategory == true;

    public static TopicDetails FromJson(JsonElement el)
    {
        var d = new TopicDetails
        {
            CanEdit = JsonFlexible.Prop(el, "can_edit", JsonFlexible.GetBool),
            CanCreatePost = JsonFlexible.Prop(el, "can_create_post", JsonFlexible.GetBool),
            CanFlagTopic = JsonFlexible.Prop(el, "can_flag_topic", JsonFlexible.GetBool),
            CanCloseTopic = JsonFlexible.Prop(el, "can_close_topic", JsonFlexible.GetBool),
            CanArchiveTopic = JsonFlexible.Prop(el, "can_archive_topic", JsonFlexible.GetBool),
            CanPinTopic = JsonFlexible.Prop(el, "can_pin_topic", JsonFlexible.GetBool),
            CanModerateCategory = JsonFlexible.Prop(el, "can_moderate_category", JsonFlexible.GetBool)
        };
        if (el.TryGetProperty("created_by", out var cb) && cb.ValueKind == JsonValueKind.Object)
            d.CreatedBy = DiscourseUser.FromJson(cb);
        if (el.TryGetProperty("last_poster", out var lp) && lp.ValueKind == JsonValueKind.Object)
            d.LastPoster = DiscourseUser.FromJson(lp);
        if (el.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
            d.Participants = JsonFlexible.DecodeLossyArray(parts, DiscourseUser.FromJson);
        return d;
    }
}

public sealed class PostStream
{
    public List<DiscoursePost> Posts { get; set; } = [];
    public List<int>? Stream { get; set; }

    public static PostStream FromJson(JsonElement el)
    {
        var ps = new PostStream();
        if (el.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array)
            ps.Posts = JsonFlexible.DecodeLossyArray(posts, DiscoursePost.FromJson);
        if (el.TryGetProperty("stream", out var stream))
            ps.Stream = JsonFlexible.GetIntArray(stream);
        return ps;
    }
}

public sealed class TopicDetailResponse
{
    public PostStream PostStream { get; set; } = new();
    public int Id { get; set; }
    public string Title { get; set; } = "(无标题)";
    public string? FancyTitle { get; set; }
    public int? PostsCount { get; set; }
    public string? CreatedAt { get; set; }
    public int? Views { get; set; }
    public int? ReplyCount { get; set; }
    public int? LikeCount { get; set; }
    public int? CategoryId { get; set; }
    public List<string>? Tags { get; set; }
    public string? Slug { get; set; }
    public TopicDetails? Details { get; set; }
    public bool? Bookmarked { get; set; }
    public bool? Closed { get; set; }
    public bool? Archived { get; set; }
    public int? HighestPostNumber { get; set; }
    public int? NotificationLevel { get; set; }
    public int? LastReadPostNumber { get; set; }
    public bool? SharedIssueVisible { get; set; }
    public bool? CanCreateSharedIssue { get; set; }
    public int? SharedIssueCount { get; set; }
    public bool? UserCreatedSharedIssue { get; set; }
    public bool? HasAcceptedAnswer { get; set; }

    public string DisplayTitle =>
        !string.IsNullOrEmpty(FancyTitle) ? FancyTitle! : Title;

    public TopicNotificationLevel NotificationLevelValue =>
        Enum.IsDefined(typeof(TopicNotificationLevel), NotificationLevel ?? 1)
            ? (TopicNotificationLevel)(NotificationLevel ?? 1)
            : TopicNotificationLevel.Regular;

    public static TopicDetailResponse FromJson(JsonElement root)
    {
        var r = new TopicDetailResponse
        {
            Id = JsonFlexible.Prop(root, "id", JsonFlexible.GetInt) ?? 0,
            Title = JsonFlexible.Prop(root, "title", JsonFlexible.GetString) ?? "(无标题)",
            FancyTitle = JsonFlexible.Prop(root, "fancy_title", JsonFlexible.GetString),
            PostsCount = JsonFlexible.Prop(root, "posts_count", JsonFlexible.GetInt),
            CreatedAt = JsonFlexible.Prop(root, "created_at", JsonFlexible.GetString),
            Views = JsonFlexible.Prop(root, "views", JsonFlexible.GetInt),
            ReplyCount = JsonFlexible.Prop(root, "reply_count", JsonFlexible.GetInt),
            LikeCount = JsonFlexible.Prop(root, "like_count", JsonFlexible.GetInt),
            CategoryId = JsonFlexible.Prop(root, "category_id", JsonFlexible.GetInt),
            Slug = JsonFlexible.Prop(root, "slug", JsonFlexible.GetString),
            Bookmarked = JsonFlexible.Prop(root, "bookmarked", JsonFlexible.GetBool),
            Closed = JsonFlexible.Prop(root, "closed", JsonFlexible.GetBool),
            Archived = JsonFlexible.Prop(root, "archived", JsonFlexible.GetBool),
            HighestPostNumber = JsonFlexible.Prop(root, "highest_post_number", JsonFlexible.GetInt),
            NotificationLevel = JsonFlexible.Prop(root, "notification_level", JsonFlexible.GetInt),
            LastReadPostNumber = JsonFlexible.Prop(root, "last_read_post_number", JsonFlexible.GetInt),
            SharedIssueVisible = JsonFlexible.Prop(root, "shared_issue_visible", JsonFlexible.GetBool),
            CanCreateSharedIssue = JsonFlexible.Prop(root, "can_create_shared_issue", JsonFlexible.GetBool),
            SharedIssueCount = JsonFlexible.Prop(root, "shared_issue_count", JsonFlexible.GetInt),
            UserCreatedSharedIssue = JsonFlexible.Prop(root, "user_created_shared_issue", JsonFlexible.GetBool),
            HasAcceptedAnswer = JsonFlexible.Prop(root, "has_accepted_answer", JsonFlexible.GetBool)
        };
        if (root.TryGetProperty("tags", out var tags))
            r.Tags = JsonFlexible.GetStringArray(tags);
        if (root.TryGetProperty("post_stream", out var ps) && ps.ValueKind == JsonValueKind.Object)
            r.PostStream = PostStream.FromJson(ps);
        if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
            r.Details = TopicDetails.FromJson(details);
        return r;
    }
}

public enum TopicNotificationLevel
{
    Muted = 0,
    Regular = 1,
    Tracking = 2,
    Watching = 3,
    WatchingFirstPost = 4
}

public static class TopicNotificationLevelExtensions
{
    public static string Title(this TopicNotificationLevel level) => level switch
    {
        TopicNotificationLevel.Muted => "静音",
        TopicNotificationLevel.Regular => "正常",
        TopicNotificationLevel.Tracking => "跟踪",
        TopicNotificationLevel.Watching => "监视",
        TopicNotificationLevel.WatchingFirstPost => "仅监视一楼",
        _ => "正常"
    };
}

public sealed class CreatePostResponse
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Username { get; set; }
    public string? AvatarTemplate { get; set; }
    public string? CreatedAt { get; set; }
    public string? Cooked { get; set; }
    public int? PostNumber { get; set; }
    public int? TopicId { get; set; }
    public string? TopicSlug { get; set; }

    public static CreatePostResponse FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
        CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
        Cooked = JsonFlexible.Prop(el, "cooked", JsonFlexible.GetString),
        PostNumber = JsonFlexible.Prop(el, "post_number", JsonFlexible.GetInt),
        TopicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt),
        TopicSlug = JsonFlexible.Prop(el, "topic_slug", JsonFlexible.GetString)
    };
}

public sealed class CurrentUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "user";
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }
    public int? UnreadNotifications { get; set; }
    public int? UnreadHighPriorityNotifications { get; set; }
    public int? AllUnreadNotifications { get; set; }
    public int? TrustLevel { get; set; }
    public bool? Moderator { get; set; }
    public bool? Admin { get; set; }
    public string? Title { get; set; }
    public int? NotificationChannelPosition { get; set; }

    public int TotalUnread =>
        AllUnreadNotifications ?? ((UnreadNotifications ?? 0) + (UnreadHighPriorityNotifications ?? 0));

    public Uri? AvatarUrl(Uri baseUrl, int size = 120)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public static CurrentUser? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return new CurrentUser
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString) ?? "user",
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
            AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
            UnreadNotifications = JsonFlexible.Prop(el, "unread_notifications", JsonFlexible.GetInt),
            UnreadHighPriorityNotifications = JsonFlexible.Prop(el, "unread_high_priority_notifications", JsonFlexible.GetInt),
            AllUnreadNotifications = JsonFlexible.Prop(el, "all_unread_notifications_count", JsonFlexible.GetInt),
            TrustLevel = JsonFlexible.Prop(el, "trust_level", JsonFlexible.GetInt),
            Moderator = JsonFlexible.Prop(el, "moderator", JsonFlexible.GetBool),
            Admin = JsonFlexible.Prop(el, "admin", JsonFlexible.GetBool),
            Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
            NotificationChannelPosition = JsonFlexible.Prop(el, "notification_channel_position", JsonFlexible.GetInt)
        };
    }
}

public sealed class DiscourseNotification
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public int? NotificationType { get; set; }
    public bool? Read { get; set; }
    public string? CreatedAt { get; set; }
    public int? PostNumber { get; set; }
    public int? TopicId { get; set; }
    public string? FancyTitle { get; set; }
    public string? Slug { get; set; }
    public string? DisplayUsername { get; set; }
    public string? TopicTitle { get; set; }

    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public string Title
    {
        get
        {
            if (!string.IsNullOrEmpty(FancyTitle)) return FancyTitle!;
            if (!string.IsNullOrEmpty(TopicTitle)) return TopicTitle!;
            return DisplayUsername is not null ? $"来自 {DisplayUsername}" : "通知";
        }
    }

    public string TypeDescription => NotificationType switch
    {
        1 => "提到了你",
        2 => "回复了你",
        3 => "引用了你",
        4 => "编辑了",
        5 => "点赞了",
        6 => "私信",
        9 => "邀请",
        12 => "徽章",
        _ => "通知"
    };

    public static DiscourseNotification FromJson(JsonElement el)
    {
        string? displayUsername = null;
        string? topicTitle = null;
        if (el.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            displayUsername = JsonFlexible.Prop(data, "display_username", JsonFlexible.GetString)
                ?? JsonFlexible.Prop(data, "username", JsonFlexible.GetString);
            topicTitle = JsonFlexible.Prop(data, "topic_title", JsonFlexible.GetString);
        }

        return new DiscourseNotification
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            UserId = JsonFlexible.Prop(el, "user_id", JsonFlexible.GetInt),
            NotificationType = JsonFlexible.Prop(el, "notification_type", JsonFlexible.GetInt),
            Read = JsonFlexible.Prop(el, "read", JsonFlexible.GetBool),
            CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
            PostNumber = JsonFlexible.Prop(el, "post_number", JsonFlexible.GetInt),
            TopicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt),
            FancyTitle = JsonFlexible.Prop(el, "fancy_title", JsonFlexible.GetString),
            Slug = JsonFlexible.Prop(el, "slug", JsonFlexible.GetString),
            DisplayUsername = displayUsername,
            TopicTitle = topicTitle
        };
    }
}

public sealed class SearchPost
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Username { get; set; }
    public string? AvatarTemplate { get; set; }
    public string? CreatedAt { get; set; }
    public int? LikeCount { get; set; }
    public string? Blurb { get; set; }
    public int? PostNumber { get; set; }
    public int? TopicId { get; set; }

    public static SearchPost FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
        CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
        LikeCount = JsonFlexible.Prop(el, "like_count", JsonFlexible.GetInt),
        Blurb = JsonFlexible.Prop(el, "blurb", JsonFlexible.GetString),
        PostNumber = JsonFlexible.Prop(el, "post_number", JsonFlexible.GetInt),
        TopicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt)
    };
}

public sealed class SearchResponse
{
    public List<SearchPost>? Posts { get; set; }
    public List<DiscourseTopic>? Topics { get; set; }
    public List<DiscourseUser>? Users { get; set; }
    public List<DiscourseCategory>? Categories { get; set; }

    public static SearchResponse FromJson(JsonElement root)
    {
        var r = new SearchResponse();
        if (root.TryGetProperty("posts", out var posts) && posts.ValueKind == JsonValueKind.Array)
            r.Posts = JsonFlexible.DecodeLossyArray(posts, e => SearchPost.FromJson(e));
        if (root.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            r.Topics = JsonFlexible.DecodeLossyArray(topics, DiscourseTopic.FromJson);
        if (root.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            r.Users = JsonFlexible.DecodeLossyArray(users, DiscourseUser.FromJson);
        if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            r.Categories = JsonFlexible.DecodeLossyArray(cats, DiscourseCategory.FromJson);
        return r;
    }
}

public sealed class DiscourseTag
{
    public int? IdNumber { get; set; }
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
    public int? Count { get; set; }
    public bool? PmOnly { get; set; }
    public string Id => Name;

    public static DiscourseTag FromJson(JsonElement el) => new()
    {
        IdNumber = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString) ?? "",
        Slug = JsonFlexible.Prop(el, "slug", JsonFlexible.GetString),
        Count = JsonFlexible.Prop(el, "count", JsonFlexible.GetInt),
        PmOnly = JsonFlexible.Prop(el, "pm_only", JsonFlexible.GetBool)
    };
}

public sealed class DiscourseBookmark
{
    public int Id { get; set; }
    public string? CreatedAt { get; set; }
    public string? Name { get; set; }
    public string? ReminderAt { get; set; }
    public string? Title { get; set; }
    public string? FancyTitle { get; set; }
    public string? Excerpt { get; set; }
    public int? BookmarkableId { get; set; }
    public string? BookmarkableType { get; set; }
    public int? TopicId { get; set; }
    public int? LinkedPostNumber { get; set; }
    public List<string>? Tags { get; set; }
    public string? Slug { get; set; }

    public string DisplayTitle =>
        !string.IsNullOrEmpty(FancyTitle) ? FancyTitle! :
        Title ?? Name ?? "书签";

    public string ListTitle =>
        !string.IsNullOrEmpty(Name) ? Name! : DisplayTitle;

    public DateTimeOffset? CreatedDate => DiscourseDateParser.Parse(CreatedAt);

    public static DiscourseBookmark FromJson(JsonElement el)
    {
        var type = JsonFlexible.Prop(el, "bookmarkable_type", JsonFlexible.GetString);
        var bookmarkableId = JsonFlexible.Prop(el, "bookmarkable_id", JsonFlexible.GetInt);
        var topicId = JsonFlexible.Prop(el, "topic_id", JsonFlexible.GetInt);
        if (topicId is null && string.Equals(type, "topic", StringComparison.OrdinalIgnoreCase))
            topicId = bookmarkableId;

        return new DiscourseBookmark
        {
            Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
            CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
            Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
            ReminderAt = JsonFlexible.Prop(el, "reminder_at", JsonFlexible.GetString),
            Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
            FancyTitle = JsonFlexible.Prop(el, "fancy_title", JsonFlexible.GetString),
            Excerpt = JsonFlexible.Prop(el, "excerpt", JsonFlexible.GetString),
            BookmarkableId = bookmarkableId,
            BookmarkableType = type,
            TopicId = topicId,
            LinkedPostNumber = JsonFlexible.Prop(el, "linked_post_number", JsonFlexible.GetInt),
            Tags = el.TryGetProperty("tags", out var tags) ? JsonFlexible.GetStringArray(tags) : null,
            Slug = JsonFlexible.Prop(el, "slug", JsonFlexible.GetString)
        };
    }
}

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Username { get; set; } = "user";
    public string? Name { get; set; }
    public string? AvatarTemplate { get; set; }
    public string? BioRaw { get; set; }
    public string? BioCooked { get; set; }
    public string? BioExcerpt { get; set; }
    public int? TrustLevel { get; set; }
    public bool? Moderator { get; set; }
    public bool? Admin { get; set; }
    public string? Title { get; set; }
    public string? CreatedAt { get; set; }
    public string? LastSeenAt { get; set; }
    public string? LastPostedAt { get; set; }
    public int? BadgeCount { get; set; }
    public int? TimeRead { get; set; }
    public int? ProfileViewCount { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }
    public bool? Ignored { get; set; }
    public bool? Muted { get; set; }
    public bool? CanSendPrivateMessage { get; set; }
    public bool? CanFollow { get; set; }
    public bool? IsFollowed { get; set; }
    public int? TotalFollowers { get; set; }
    public int? TotalFollowing { get; set; }

    public string DisplayName =>
        !string.IsNullOrEmpty(Name) ? Name! : Username;

    public Uri? AvatarUrl(Uri baseUrl, int size = 240)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public static UserProfile FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt) ?? 0,
        Username = JsonFlexible.Prop(el, "username", JsonFlexible.GetString) ?? "user",
        Name = JsonFlexible.Prop(el, "name", JsonFlexible.GetString),
        AvatarTemplate = JsonFlexible.Prop(el, "avatar_template", JsonFlexible.GetString),
        BioRaw = JsonFlexible.Prop(el, "bio_raw", JsonFlexible.GetString),
        BioCooked = JsonFlexible.Prop(el, "bio_cooked", JsonFlexible.GetString),
        BioExcerpt = JsonFlexible.Prop(el, "bio_excerpt", JsonFlexible.GetString),
        TrustLevel = JsonFlexible.Prop(el, "trust_level", JsonFlexible.GetInt),
        Moderator = JsonFlexible.Prop(el, "moderator", JsonFlexible.GetBool),
        Admin = JsonFlexible.Prop(el, "admin", JsonFlexible.GetBool),
        Title = JsonFlexible.Prop(el, "title", JsonFlexible.GetString),
        CreatedAt = JsonFlexible.Prop(el, "created_at", JsonFlexible.GetString),
        LastSeenAt = JsonFlexible.Prop(el, "last_seen_at", JsonFlexible.GetString),
        LastPostedAt = JsonFlexible.Prop(el, "last_posted_at", JsonFlexible.GetString),
        BadgeCount = JsonFlexible.Prop(el, "badge_count", JsonFlexible.GetInt),
        TimeRead = JsonFlexible.Prop(el, "time_read", JsonFlexible.GetInt),
        ProfileViewCount = JsonFlexible.Prop(el, "profile_view_count", JsonFlexible.GetInt),
        Website = JsonFlexible.Prop(el, "website", JsonFlexible.GetString),
        Location = JsonFlexible.Prop(el, "location", JsonFlexible.GetString),
        Ignored = JsonFlexible.Prop(el, "ignored", JsonFlexible.GetBool),
        Muted = JsonFlexible.Prop(el, "muted", JsonFlexible.GetBool),
        CanSendPrivateMessage = JsonFlexible.Prop(el, "can_send_private_message_to_user", JsonFlexible.GetBool),
        CanFollow = JsonFlexible.Prop(el, "can_follow", JsonFlexible.GetBool),
        IsFollowed = JsonFlexible.Prop(el, "is_followed", JsonFlexible.GetBool),
        TotalFollowers = JsonFlexible.Prop(el, "total_followers", JsonFlexible.GetInt),
        TotalFollowing = JsonFlexible.Prop(el, "total_following", JsonFlexible.GetInt)
    };
}

public sealed class UploadResponse
{
    public int? Id { get; set; }
    public string? Url { get; set; }
    public string? OriginalFilename { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ShortUrl { get; set; }
    public string? ShortPath { get; set; }

    public string Markdown
    {
        get
        {
            var name = string.IsNullOrEmpty(OriginalFilename) ? "image" : OriginalFilename!;
            if (!string.IsNullOrEmpty(ShortUrl))
            {
                if (Width is > 0 && Height is > 0)
                    return $"![{name}|{Width}x{Height}]({ShortUrl})";
                return $"![{name}]({ShortUrl})";
            }
            if (!string.IsNullOrEmpty(ShortPath)) return $"![{name}]({ShortPath})";
            if (!string.IsNullOrEmpty(Url))
            {
                if (Width is > 0 && Height is > 0)
                    return $"![{name}|{Width}x{Height}]({Url})";
                return $"![{name}]({Url})";
            }
            return "";
        }
    }

    public static UploadResponse FromJson(JsonElement el) => new()
    {
        Id = JsonFlexible.Prop(el, "id", JsonFlexible.GetInt),
        Url = JsonFlexible.Prop(el, "url", JsonFlexible.GetString),
        OriginalFilename = JsonFlexible.Prop(el, "original_filename", JsonFlexible.GetString),
        Width = JsonFlexible.Prop(el, "width", JsonFlexible.GetInt),
        Height = JsonFlexible.Prop(el, "height", JsonFlexible.GetInt),
        ShortUrl = JsonFlexible.Prop(el, "short_url", JsonFlexible.GetString),
        ShortPath = JsonFlexible.Prop(el, "short_path", JsonFlexible.GetString)
    };
}

public sealed class LocalTopicItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public int? CategoryId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? AuthorUsername { get; set; }
    public int? PostsCount { get; set; }
    public int? Views { get; set; }
    public int? LikeCount { get; set; }
    public DateTimeOffset LastVisitedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? SavedAt { get; set; }
    public string? Note { get; set; }
}

public enum AuthMethod { Cookie, UserApiKey }

public sealed class AuthSession
{
    public AuthMethod Method { get; set; }
    public string Username { get; set; } = "";
    public int? UserId { get; set; }
    public string? AvatarTemplate { get; set; }
    public string? ApiKey { get; set; }
    public string? ClientId { get; set; }

    public Uri? AvatarUrl(Uri baseUrl, int size = 90)
        => AvatarUrlHelper.Make(AvatarTemplate, baseUrl, size);

    public static AuthSession Cookie(string username, int? userId, string? avatarTemplate = null) => new()
    {
        Method = AuthMethod.Cookie,
        Username = username,
        UserId = userId,
        AvatarTemplate = avatarTemplate
    };

    public static AuthSession UserApi(string apiKey, string username, string clientId, int? userId, string? avatarTemplate = null) => new()
    {
        Method = AuthMethod.UserApiKey,
        Username = username,
        UserId = userId,
        AvatarTemplate = avatarTemplate,
        ApiKey = apiKey,
        ClientId = clientId
    };
}

public sealed class ComposeDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int? CategoryId { get; set; }
    public int? TopicId { get; set; }
    public int? ReplyToPostNumber { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ServerKey { get; set; }
    public int? ServerSequence { get; set; }
    public bool? IsPrivateMessage { get; set; }
    public string? PmRecipients { get; set; }
    public List<string>? Tags { get; set; }

    public bool IsReply => TopicId is not null;
    public bool IsServerBacked => ServerKey is not null;

    public static ComposeDraft FromServer(ServerDraft server)
    {
        var payload = server.Data;
        int? topicId = null;
        if (server.DraftKey.StartsWith("topic_", StringComparison.Ordinal))
        {
            var parts = server.DraftKey.Split('_');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var tid))
                topicId = tid;
        }
        return new ComposeDraft
        {
            Id = Guid.NewGuid(),
            Title = payload?.Title ?? "",
            Body = payload?.Reply ?? "",
            CategoryId = payload?.CategoryId,
            TopicId = topicId,
            ReplyToPostNumber = payload?.ReplyToPostNumber,
            UpdatedAt = server.CreatedDate ?? DateTimeOffset.Now,
            ServerKey = server.DraftKey,
            ServerSequence = server.Sequence,
            IsPrivateMessage = payload?.ArchetypeId == "private_message"
                               || server.DraftKey.Contains("private", StringComparison.OrdinalIgnoreCase),
            PmRecipients = payload?.Recipients is { Count: > 0 }
                ? string.Join(", ", payload.Recipients)
                : null,
            Tags = payload?.Tags
        };
    }
}

public sealed class ComposeContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int? CategoryId { get; set; }
    public int? TopicId { get; set; }
    public int? ReplyToPostNumber { get; set; }
    public string? TopicTitle { get; set; }
    public Guid? DraftId { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool IsPrivateMessage { get; set; }
    public string PmRecipients { get; set; } = "";
    public int? EditPostId { get; set; }

    public bool IsReply => TopicId is not null && EditPostId is null;
    public bool IsEditing => EditPostId is not null;

    public static ComposeContext NewTopic(int? categoryId = null) => new() { CategoryId = categoryId };
    public static ComposeContext PrivateMessage(string? to = null) => new()
    {
        IsPrivateMessage = true,
        PmRecipients = to ?? ""
    };
    public static ComposeContext Reply(int topicId, string? topicTitle, int? postNumber = null, string? quote = null) => new()
    {
        TopicId = topicId,
        TopicTitle = topicTitle,
        ReplyToPostNumber = postNumber,
        Body = quote ?? ""
    };
    public static ComposeContext Edit(int postId, string raw, int? topicId, string? topicTitle) => new()
    {
        EditPostId = postId,
        Body = raw,
        TopicId = topicId,
        TopicTitle = topicTitle
    };
    public static ComposeContext FromDraft(ComposeDraft draft) => new()
    {
        Title = draft.Title,
        Body = draft.Body,
        CategoryId = draft.CategoryId,
        TopicId = draft.TopicId,
        ReplyToPostNumber = draft.ReplyToPostNumber,
        DraftId = draft.Id,
        Tags = draft.Tags ?? [],
        IsPrivateMessage = draft.IsPrivateMessage == true,
        PmRecipients = draft.PmRecipients ?? ""
    };
}
