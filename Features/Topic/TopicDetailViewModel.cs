using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Features.Topic;

public sealed class TableCellVm
{
    public string Text { get; init; } = "";
    public bool IsHeader { get; init; }
}

public sealed class TableRowVm
{
    public IReadOnlyList<TableCellVm> Cells { get; init; } = [];
    public bool IsHeader { get; init; }
}

public partial class ContentBlockVm : ObservableObject
{
    public string Kind { get; init; } = "text";
    public string Text { get; set; } = "";
    /// <summary>Original HTML fragment for inline rich rendering (bold/links).</summary>
    public string? HtmlSource { get; init; }
    public string? ImageUrl { get; init; }
    public string? LinkUrl { get; init; }
    public string? QuoteAuthor { get; set; }
    public IReadOnlyList<InlineSpan>? InlineSpans { get; init; }
    public IReadOnlyList<TableRowVm>? TableRows { get; init; }
    public bool HasTable => TableRows is { Count: > 0 };
    public bool IsCode { get; init; }
    public bool IsQuote { get; init; }
    public bool IsSpoiler { get; init; }
    public bool IsHeading { get; init; }
    public bool IsImage { get; init; }
    public bool IsLink { get; init; }
    public bool IsHr { get; init; }
    public bool IsList { get; init; }
    public bool IsTable { get; init; }
    public bool IsVideo { get; init; }
    public int HeadingLevel { get; init; }
    public double FontSize { get; init; } = 15;
    public double LineHeight => FontSize * AppSettings.Current.ReadingLineSpacing + 6;
    public bool IsLongText => !IsImage && !IsHr && !IsLink && Text.Length > 800;
    public bool SupportsCollapse => AppSettings.Current.CollapseLongPosts && IsLongText;
    [ObservableProperty] private bool _isCollapsed;
    [ObservableProperty] private string? _linkExcerpt;
    [ObservableProperty] private string? _linkImageUrl;
    [ObservableProperty] private bool _isLinkLoading;
    public bool ShowPlainText => !IsImage && !IsQuote && !IsCode && !IsLink && !IsHr && !IsSpoiler && !IsCollapsed;
    public bool ShowExpandButton => SupportsCollapse && IsCollapsed;
    public bool ShowCollapseButton => SupportsCollapse && !IsCollapsed;
    public bool HasLinkImage => !string.IsNullOrEmpty(LinkImageUrl);
    public string CollapsedPreview => Text.Length > 280 ? Text[..280] + "…" : Text;
    public void ApplyCollapseDefault() => IsCollapsed = SupportsCollapse;

    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlainText));
        OnPropertyChanged(nameof(ShowExpandButton));
        OnPropertyChanged(nameof(ShowCollapseButton));
    }

    partial void OnLinkImageUrlChanged(string? value)
        => OnPropertyChanged(nameof(HasLinkImage));

    public async Task EnrichOneboxAsync()
    {
        if (!IsLink || string.IsNullOrEmpty(LinkUrl)) return;
        if (!Uri.TryCreate(LinkUrl, UriKind.Absolute, out var uri)) return;
        try
        {
            IsLinkLoading = true;
            var preview = OneboxService.Shared.Cached(uri) ?? await OneboxService.Shared.PreviewAsync(uri);
            if (preview is null) return;
            if (!string.IsNullOrEmpty(preview.Title))
                Text = preview.Title;
            if (!string.IsNullOrEmpty(preview.SiteName))
                QuoteAuthor = preview.SiteName;
            LinkExcerpt = preview.Description;
            LinkImageUrl = preview.ImageUrl?.AbsoluteUri;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(QuoteAuthor));
        }
        catch
        {
            // keep original card
        }
        finally
        {
            IsLinkLoading = false;
        }
    }
}

public partial class PollOptionVm : ObservableObject
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    [ObservableProperty] private int _votes;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _fraction;
}

public partial class PollVm : ObservableObject
{
    public string Name { get; init; } = "";
    public bool IsMultiple { get; init; }
    public bool IsOpen { get; init; }
    [ObservableProperty] private int _voters;
    public ObservableCollection<PollOptionVm> Options { get; } = [];
    public PostItemViewModel Owner { get; init; } = null!;
}

public partial class PostItemViewModel : ObservableObject
{
    [ObservableProperty] private DiscoursePost _post;
    public Uri BaseUrl { get; }
    public ObservableCollection<ContentBlockVm> Blocks { get; } = [];
    public ObservableCollection<PollVm> Polls { get; } = [];
    public ObservableCollection<string> ReactionChips { get; } = [];
    public ObservableCollection<BoostItemVm> Boosts { get; } = [];

    public PostItemViewModel(DiscoursePost post, Uri baseUrl)
    {
        _post = post;
        BaseUrl = baseUrl;
        RebuildBlocks();
        RebuildPolls();
        RebuildReactions();
        RebuildBoosts();
    }

    public int Id => Post.Id;
    public int Floor => Post.PostNumber ?? 0;
    public string AuthorName => Post.AuthorUsername ?? "用户";
    public string? AvatarUrl => Post.AvatarUrl(BaseUrl)?.AbsoluteUri;
    public string? UserTitle => Post.UserTitle;
    public string TimeText => RelativeDate.Describe(Post.CreatedDate);
    public string LikeLabel => Post.LikeCount > 0 ? $"❤ {Post.LikeCount}" : "❤ 赞";
    public bool IsLiked => Post.Liked;
    public bool CanEdit => Post.CanEdit == true;
    public bool CanDelete => Post.CanDelete == true;
    public bool CanRecover => Post.CanRecover == true;
    public bool CanAccept => Post.CanAcceptAnswer == true;
    public bool IsBookmarked => Post.Bookmarked == true;
    public bool IsAccepted => Post.AcceptedAnswer == true;
    public bool IsDeleted => Post.UserDeleted == true || !string.IsNullOrEmpty(Post.DeletedAt);
    public bool IsStaff => Post.Moderator == true || Post.Admin == true;
    public string FloorLabel => $"#{Floor}";
    public string? MyReaction => Post.CurrentUserReaction?.Id;
    public bool HasPolls => Polls.Count > 0;
    public bool HasReactions => ReactionChips.Count > 0;
    public bool HasBoosts => Boosts.Count > 0;

    public void RebuildBlocks()
    {
        Blocks.Clear();
        var font = AppSettings.Current.BodyFontSize;
        var html = Post.Cooked;
        if (string.IsNullOrEmpty(html))
        {
            if (!string.IsNullOrEmpty(Post.Raw))
            {
                var raw = new ContentBlockVm { Kind = "text", Text = Post.Raw, FontSize = font };
                raw.ApplyCollapseDefault();
                Blocks.Add(raw);
            }
            return;
        }

        foreach (var b in PostContentCache.GetOrParse(html, BaseUrl))
        {
            ContentBlockVm? vm = b switch
            {
                PostContentParser.Block.Text t =>
                    new ContentBlockVm
                    {
                        Kind = "text",
                        Text = t.Content,
                        HtmlSource = t.Html,
                        InlineSpans = string.IsNullOrEmpty(t.Html) ? null : InlineText.FromHtml(t.Html),
                        FontSize = font
                    },
                PostContentParser.Block.Heading h =>
                    new ContentBlockVm
                    {
                        Kind = "heading", Text = h.Content, IsHeading = true,
                        HeadingLevel = h.Level, FontSize = font + (4 - Math.Min(h.Level, 3)),
                        InlineSpans = InlineText.FromPlain(h.Content)
                    },
                PostContentParser.Block.ListItem li =>
                    new ContentBlockVm
                    {
                        Kind = "list",
                        Text = (li.Ordered ? $"{li.Index}. " : "• ") + li.Content,
                        IsList = true, FontSize = font,
                        InlineSpans = InlineText.FromPlain((li.Ordered ? $"{li.Index}. " : "• ") + li.Content)
                    },
                PostContentParser.Block.Image img =>
                    new ContentBlockVm
                    {
                        Kind = "image", ImageUrl = img.Url.AbsoluteUri, Text = img.Alt ?? "", IsImage = true
                    },
                PostContentParser.Block.LinkCard card =>
                    new ContentBlockVm
                    {
                        Kind = "link", Text = card.Title, LinkUrl = card.Url.AbsoluteUri,
                        QuoteAuthor = card.Host, IsLink = true, FontSize = font
                    },
                PostContentParser.Block.Quote q =>
                    new ContentBlockVm
                    {
                        Kind = "quote", Text = q.Content, QuoteAuthor = q.Author ?? q.Title,
                        LinkUrl = q.TopicUrl?.AbsoluteUri, IsQuote = true, FontSize = font - 1,
                        InlineSpans = InlineText.FromPlain(q.Content)
                    },
                PostContentParser.Block.Code c =>
                    new ContentBlockVm { Kind = "code", Text = c.Content, IsCode = true, FontSize = font - 1 },
                PostContentParser.Block.Spoiler s =>
                    new ContentBlockVm { Kind = "spoiler", Text = s.Content, IsSpoiler = true, FontSize = font },
                PostContentParser.Block.Table table => BuildTableVm(table, font),
                PostContentParser.Block.Video v =>
                    new ContentBlockVm { Kind = "video", Text = "🎬 视频", LinkUrl = v.Url.AbsoluteUri, IsVideo = true, FontSize = font },
                PostContentParser.Block.HorizontalRule =>
                    new ContentBlockVm { Kind = "hr", IsHr = true },
                _ => null
            };
            if (vm is null) continue;
            vm.ApplyCollapseDefault();
            Blocks.Add(vm);
            if (vm.IsLink)
                _ = vm.EnrichOneboxAsync();
        }
    }

    private static ContentBlockVm BuildTableVm(PostContentParser.Block.Table table, double font)
    {
        var rows = new List<TableRowVm>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var isHeader = i == 0;
            var cells = table.Rows[i]
                .Select(c => new TableCellVm { Text = c, IsHeader = isHeader })
                .ToList();
            rows.Add(new TableRowVm { Cells = cells, IsHeader = isHeader });
        }
        return new ContentBlockVm
        {
            Kind = "table",
            Text = string.Join("\n", table.Rows.Select(r => string.Join(" | ", r))),
            IsTable = true,
            FontSize = font - 1,
            TableRows = rows
        };
    }

    public void RebuildPolls()
    {
        Polls.Clear();
        if (Post.Polls is not { Count: > 0 }) return;
        foreach (var poll in Post.Polls)
        {
            var selected = Post.PollsVotes?.GetValueOrDefault(poll.Name) ?? [];
            var total = Math.Max(poll.Voters, poll.Options.Sum(o => o.Votes));
            var vm = new PollVm
            {
                Name = poll.Name,
                IsMultiple = poll.IsMultiple,
                IsOpen = poll.IsOpen,
                Voters = poll.Voters,
                Owner = this
            };
            foreach (var opt in poll.Options)
            {
                vm.Options.Add(new PollOptionVm
                {
                    Id = opt.Id,
                    Label = opt.PlainLabel,
                    Votes = opt.Votes,
                    IsSelected = selected.Contains(opt.Id),
                    Fraction = poll.CanSeeResults && total > 0 ? (double)opt.Votes / total : 0
                });
            }
            Polls.Add(vm);
        }
        OnPropertyChanged(nameof(HasPolls));
    }

    public void RebuildReactions()
    {
        ReactionChips.Clear();
        if (Post.Reactions is { Count: > 0 })
        {
            foreach (var r in Post.Reactions.Where(r => r.Count > 0))
                ReactionChips.Add($"{CommonReactions.Display(r.Id)} {r.Count}");
        }
        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(MyReaction));
    }

    public void RebuildBoosts()
    {
        Boosts.Clear();
        if (Post.Boosts is { Count: > 0 })
        {
            foreach (var b in Post.Boosts)
            {
                Boosts.Add(new BoostItemVm
                {
                    Id = b.Id,
                    Author = b.User?.Username is string u ? "@" + u : "Boost",
                    Body = b.BodyText,
                    CanDelete = b.CanDelete == true,
                    Owner = this
                });
            }
        }
        OnPropertyChanged(nameof(HasBoosts));
    }

    public void Refresh()
    {
        RebuildBlocks();
        RebuildPolls();
        RebuildReactions();
        RebuildBoosts();
        OnPropertyChanged(nameof(LikeLabel));
        OnPropertyChanged(nameof(IsLiked));
        OnPropertyChanged(nameof(IsBookmarked));
        OnPropertyChanged(nameof(IsAccepted));
        OnPropertyChanged(nameof(IsDeleted));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(CanAccept));
    }
}

public sealed class BoostItemVm
{
    public int Id { get; init; }
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
    public bool CanDelete { get; init; }
    public PostItemViewModel Owner { get; init; } = null!;
}

public partial class TopicDetailViewModel : ObservableObject
{
    public const int PageSize = 50;

    public int TopicId { get; }

    [ObservableProperty] private string _title = "主题";
    [ObservableProperty] private ObservableCollection<PostItemViewModel> _posts = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isPaging;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _actionMessage;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private int _totalFloors = 1;
    [ObservableProperty] private int _currentFloor = 1;
    [ObservableProperty] private bool _bookmarked;
    [ObservableProperty] private bool _closed;
    [ObservableProperty] private bool _archived;
    [ObservableProperty] private bool _canCreatePost = true;
    [ObservableProperty] private bool _canModerate;
    [ObservableProperty] private TopicNotificationLevel _notificationLevel = TopicNotificationLevel.Regular;
    [ObservableProperty] private string? _slug;
    [ObservableProperty] private int? _views;
    [ObservableProperty] private int? _likeCount;
    [ObservableProperty] private int? _replyCount;
    [ObservableProperty] private List<string> _tags = [];
    [ObservableProperty] private int? _categoryId;
    [ObservableProperty] private int _sharedIssueCount;
    [ObservableProperty] private bool _userCreatedSharedIssue;
    [ObservableProperty] private bool _sharedIssueVisible;
    [ObservableProperty] private bool _hasAcceptedAnswer;
    [ObservableProperty] private int _pendingNewPostCount;
    [ObservableProperty] private string _jumpFloorText = "";

    private List<int> _stream = [];
    private int? _highestPostNumber;
    private int? _focusPostNumber;
    private string? _topicBusChannel;
    private readonly HashSet<int> _seenBusPostIds = [];

    public bool CanGoPreviousPage => CurrentPage > 0;
    public bool CanGoNextPage => CurrentPage < TotalPages - 1;
    public string PageLabel => $"第 {CurrentPage + 1} / {TotalPages} 页 · 共 {TotalFloors} 楼";
    public string ShareUrl
    {
        get
        {
            var baseUrl = AppSettings.Current.BaseUrl.AbsoluteUri.TrimEnd('/');
            var s = Slug ?? "topic";
            return $"{baseUrl}/t/{s}/{TopicId}";
        }
    }

    public string MarkdownExport
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {Title}");
            sb.AppendLine();
            sb.AppendLine($"> {ShareUrl}");
            sb.AppendLine();
            foreach (var p in Posts)
            {
                sb.AppendLine($"## #{p.Floor} @{p.AuthorName}");
                sb.AppendLine();
                foreach (var b in p.Blocks)
                {
                    if (b.IsImage && b.ImageUrl is not null)
                        sb.AppendLine($"![]({b.ImageUrl})");
                    else if (!string.IsNullOrEmpty(b.Text))
                        sb.AppendLine(b.Text);
                    sb.AppendLine();
                }
                sb.AppendLine("---");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public TopicDetailViewModel(int topicId, string? initialTitle = null, int? focusPostNumber = null)
    {
        TopicId = topicId;
        Title = initialTitle ?? "主题";
        _focusPostNumber = focusPostNumber;
        if (focusPostNumber is > 0) CurrentFloor = focusPostNumber.Value;
    }

    public void StartTopicBus()
    {
        _topicBusChannel = $"/topic/{TopicId}";
        MessageBusService.Shared.Configure(DiscourseAPI.Shared.CurrentBaseUrl);
        MessageBusService.Shared.Subscribe(_topicBusChannel, -1);
        AppEvents.TopicBus += OnTopicBus;
    }

    public void StopTopicBus()
    {
        AppEvents.TopicBus -= OnTopicBus;
        if (_topicBusChannel is not null)
        {
            MessageBusService.Shared.Unsubscribe(_topicBusChannel);
            _topicBusChannel = null;
        }
    }

    private void OnTopicBus(string channel, Dictionary<string, object?> data)
    {
        if (_topicBusChannel is null || channel != _topicBusChannel) return;
        App.DispatcherQueue?.TryEnqueue(() => HandleTopicBus(data));
    }

    private void HandleTopicBus(Dictionary<string, object?> data)
    {
        if (data.GetValueOrDefault("reload_topic") is true)
        {
            _ = LoadAsync(force: true);
            return;
        }
        if (ToInt(data.GetValueOrDefault("notification_level_change")) is int level)
        {
            NotificationLevel = Enum.IsDefined(typeof(TopicNotificationLevel), level)
                ? (TopicNotificationLevel)level : NotificationLevel;
            return;
        }

        var type = data.GetValueOrDefault("type") as string;
        var postId = ToInt(data.GetValueOrDefault("id"));
        switch (type)
        {
            case "created":
                if (postId is null || _seenBusPostIds.Contains(postId.Value)) return;
                _seenBusPostIds.Add(postId.Value);
                if (Posts.Any(p => p.Id == postId.Value)) return;
                if (!_stream.Contains(postId.Value)) _stream.Add(postId.Value);
                if (ReplyCount is int r) ReplyCount = r + 1;
                if (CurrentPage >= TotalPages - 1)
                    PendingNewPostCount += 1;
                else
                    PendingNewPostCount += 1;
                ActionMessage = $"有 {PendingNewPostCount} 条新回复";
                RecomputePages();
                break;
            case "stats":
                if (ToInt(data.GetValueOrDefault("posts_count")) is int pc)
                    ReplyCount = Math.Max(0, pc - 1);
                if (ToInt(data.GetValueOrDefault("highest_post_number")) is int hi)
                {
                    _highestPostNumber = hi;
                    TotalFloors = hi;
                    RecomputePages();
                }
                break;
            case "deleted":
            case "destroyed":
                if (postId is int delId)
                    Posts = new ObservableCollection<PostItemViewModel>(Posts.Where(p => p.Id != delId));
                break;
        }
    }

    [RelayCommand]
    public async Task LoadNewRepliesAsync()
    {
        PendingNewPostCount = 0;
        ActionMessage = null;
        await GoToPageAsync(TotalPages - 1);
    }

    [RelayCommand]
    public async Task LoadAsync(bool force = false)
    {
        if (IsLoading && !force) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            TopicDetailResponse detail = _focusPostNumber is > 0
                ? await DiscourseAPI.Shared.FetchTopicAsync(TopicId, _focusPostNumber)
                : await DiscourseAPI.Shared.FetchTopicAsync(TopicId);

            ApplyDetail(detail);

            if (_focusPostNumber is > 0 && _stream.Count > 0)
            {
                var idx = Math.Max(0, Math.Min(_focusPostNumber.Value - 1, _stream.Count - 1));
                CurrentPage = idx / PageSize;
            }
            else CurrentPage = 0;

            await LoadPagePostsAsync();

            ReadingHistoryStore.Current.Record(
                TopicId, Title, detail.Slug, detail.CategoryId,
                detail.Tags, detail.PostsCount, detail.Views, detail.LikeCount);

            var lastFloor = Posts.LastOrDefault()?.Floor ?? 1;
            _ = DiscourseAPI.Shared.MarkTopicReadAsync(TopicId, lastFloor);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            IsLoading = false;
            NotifyPager();
        }
    }

    [RelayCommand]
    public async Task GoToPageAsync(int page)
    {
        if (page < 0 || page >= TotalPages || IsPaging) return;
        IsPaging = true;
        try
        {
            CurrentPage = page;
            await LoadPagePostsAsync();
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            IsPaging = false;
            NotifyPager();
        }
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (CanGoNextPage) await GoToPageAsync(CurrentPage + 1);
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (CanGoPreviousPage) await GoToPageAsync(CurrentPage - 1);
    }

    [RelayCommand]
    public async Task JumpToFloorAsync()
    {
        if (!int.TryParse(JumpFloorText, out var floor) || floor < 1) return;
        floor = Math.Min(floor, TotalFloors);
        var page = Math.Max(0, (floor - 1) / PageSize);
        CurrentFloor = floor;
        await GoToPageAsync(page);
    }

    [RelayCommand]
    public async Task ToggleLikeAsync(PostItemViewModel item)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (item.IsLiked) await DiscourseAPI.Shared.UnlikePostAsync(item.Id);
            else await DiscourseAPI.Shared.LikePostAsync(item.Id);
            item.Post = item.Post.WithLikeToggled();
            item.Refresh();
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    public async Task ToggleReactionAsync(PostItemViewModel item, string reaction)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.ToggleReactionAsync(item.Id, reaction);
            item.Post = item.Post.WithReactionToggled(reaction);
            item.Refresh();
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    public async Task VotePollAsync(PostItemViewModel item, string pollName, string optionId)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            var poll = item.Post.Polls?.FirstOrDefault(p => p.Name == pollName);
            List<string> options;
            if (poll?.IsMultiple == true)
            {
                var current = item.Post.PollsVotes?.GetValueOrDefault(pollName)?.ToList() ?? [];
                if (current.Contains(optionId)) current.Remove(optionId);
                else current.Add(optionId);
                options = current;
            }
            else
            {
                options = [optionId];
            }

            var updated = options.Count == 0
                ? await DiscourseAPI.Shared.RemovePollVoteAsync(item.Id, pollName)
                : await DiscourseAPI.Shared.VotePollAsync(item.Id, pollName, options);

            if (updated is not null && item.Post.Polls is not null)
            {
                var list = item.Post.Polls.ToList();
                var idx = list.FindIndex(p => p.Name == pollName);
                if (idx >= 0) list[idx] = updated;
                var p = item.Post.Clone();
                p.Polls = list;
                var votes = p.PollsVotes is null ? new Dictionary<string, List<string>>() : new(p.PollsVotes);
                votes[pollName] = options;
                p.PollsVotes = votes;
                item.Post = p;
                item.Refresh();
            }
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task ToggleBookmarkPostAsync(PostItemViewModel item)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (item.IsBookmarked && item.Post.BookmarkId is int bid)
            {
                await DiscourseAPI.Shared.DeleteBookmarkAsync(bid);
                var p = item.Post.Clone();
                p.Bookmarked = false;
                p.BookmarkId = null;
                item.Post = p;
            }
            else
            {
                var id = await DiscourseAPI.Shared.BookmarkPostAsync(item.Id);
                var p = item.Post.Clone();
                p.Bookmarked = true;
                p.BookmarkId = id;
                item.Post = p;
            }
            item.Refresh();
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task ToggleTopicBookmarkAsync()
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (!Bookmarked)
            {
                await DiscourseAPI.Shared.BookmarkTopicAsync(TopicId);
                Bookmarked = true;
            }
            else
            {
                Bookmarked = false;
            }
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task AcceptAnswerAsync(PostItemViewModel item)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (item.IsAccepted)
                await DiscourseAPI.Shared.UnacceptAnswerAsync(item.Id);
            else
                await DiscourseAPI.Shared.AcceptAnswerAsync(item.Id);
            var p = item.Post.Clone();
            p.AcceptedAnswer = !item.IsAccepted;
            item.Post = p;
            item.Refresh();
            HasAcceptedAnswer = Posts.Any(x => x.IsAccepted);
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task RecoverPostAsync(PostItemViewModel item)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.RecoverPostAsync(item.Id);
            ActionMessage = "已恢复";
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    public async Task FlagPostAsync(PostItemViewModel item, PostFlagType flagType, string? message = null)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.FlagPostAsync(item.Id, (int)flagType, message);
            ActionMessage = "已提交举报";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    public async Task CreateBoostAsync(PostItemViewModel item, string raw)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        var text = raw.Trim();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            var boost = await DiscourseAPI.Shared.CreateBoostAsync(item.Id, text);
            var p = item.Post.Clone();
            var list = p.Boosts?.ToList() ?? [];
            list.Add(boost);
            p.Boosts = list;
            item.Post = p;
            item.Refresh();
            ActionMessage = "已发送 Boost";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    public async Task DeleteBoostAsync(PostItemViewModel item, int boostId)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.DeleteBoostAsync(boostId);
            var p = item.Post.Clone();
            p.Boosts = p.Boosts?.Where(b => b.Id != boostId).ToList();
            item.Post = p;
            item.Refresh();
            ActionMessage = "已删除 Boost";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task SetNotificationLevelAsync(TopicNotificationLevel level)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.SetTopicNotificationLevelAsync(TopicId, level);
            NotificationLevel = level;
            ActionMessage = $"通知级别：{level.Title()}";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task ToggleSharedIssueAsync()
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            var (count, user) = await DiscourseAPI.Shared.ToggleSharedIssueAsync(TopicId);
            SharedIssueCount = count;
            UserCreatedSharedIssue = user;
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task DeletePostAsync(PostItemViewModel item)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.DeletePostAsync(item.Id);
            Posts = new ObservableCollection<PostItemViewModel>(Posts.Where(p => p.Id != item.Id));
            ActionMessage = "已删除";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task CloseTopicAsync()
    {
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (Closed) await DiscourseAPI.Shared.OpenTopicAsync(TopicId);
            else await DiscourseAPI.Shared.CloseTopicAsync(TopicId);
            Closed = !Closed;
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task PinTopicAsync()
    {
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.PinTopicAsync(TopicId);
            ActionMessage = "已置顶";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    [RelayCommand]
    public async Task ArchiveTopicAsync()
    {
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            if (Archived) await DiscourseAPI.Shared.UnarchiveTopicAsync(TopicId);
            else await DiscourseAPI.Shared.ArchiveTopicAsync(TopicId);
            Archived = !Archived;
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
    }

    private void ApplyDetail(TopicDetailResponse detail)
    {
        Title = detail.DisplayTitle;
        CategoryId = detail.CategoryId;
        Tags = detail.Tags ?? [];
        Views = detail.Views;
        LikeCount = detail.LikeCount;
        ReplyCount = detail.ReplyCount;
        Bookmarked = detail.Bookmarked == true;
        Closed = detail.Closed == true;
        Archived = detail.Archived == true;
        Slug = detail.Slug;
        NotificationLevel = detail.NotificationLevelValue;
        _highestPostNumber = detail.HighestPostNumber;
        SharedIssueVisible = detail.SharedIssueVisible == true;
        SharedIssueCount = detail.SharedIssueCount ?? 0;
        UserCreatedSharedIssue = detail.UserCreatedSharedIssue == true;
        HasAcceptedAnswer = detail.HasAcceptedAnswer == true;
        CanCreatePost = detail.Details?.CanCreatePost ?? true;
        CanModerate = detail.Details?.CanModerateTopic ?? false;
        _stream = detail.PostStream.Stream ?? detail.PostStream.Posts.Select(p => p.Id).ToList();
        RecomputePages();

        if (detail.PostStream.Posts.Count > 0 && CurrentPage == 0)
        {
            var baseUrl = AppSettings.Current.BaseUrl;
            Posts = new ObservableCollection<PostItemViewModel>(
                detail.PostStream.Posts.Select(p => new PostItemViewModel(p, baseUrl)));
            if (Posts.Count > 0) CurrentFloor = Posts[0].Floor;
        }
    }

    private void RecomputePages()
    {
        TotalFloors = _highestPostNumber is > 0
            ? _highestPostNumber.Value
            : Math.Max(_stream.Count, 1);
        TotalPages = Math.Max(1, (int)Math.Ceiling(Math.Max(_stream.Count, TotalFloors) / (double)PageSize));
        NotifyPager();
    }

    private void NotifyPager()
    {
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
    }

    private async Task LoadPagePostsAsync()
    {
        if (_stream.Count == 0) return;

        var start = CurrentPage * PageSize;
        if (start >= _stream.Count) start = Math.Max(0, _stream.Count - PageSize);
        var ids = _stream.Skip(start).Take(PageSize).ToList();
        if (ids.Count == 0) return;

        if (Posts.Count > 0 && Posts.All(p => ids.Contains(p.Id)) && Posts.Count == ids.Count)
            return;

        var fetched = await DiscourseAPI.Shared.FetchPostsAsync(TopicId, ids);
        var map = fetched.ToDictionary(p => p.Id);
        var ordered = ids.Where(map.ContainsKey).Select(id => map[id]).ToList();
        var baseUrl = AppSettings.Current.BaseUrl;
        Posts = new ObservableCollection<PostItemViewModel>(
            ordered.Select(p => new PostItemViewModel(p, baseUrl)));
        if (Posts.Count > 0) CurrentFloor = Posts[0].Floor;
    }

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        true => 1,
        _ => null
    };
}
