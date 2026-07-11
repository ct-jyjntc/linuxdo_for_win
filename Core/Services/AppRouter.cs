using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;

namespace LinuxDo.Core.Services;

public enum AppRouteKind
{
    Latest, Top, New, Unread, Categories, Category, Tags, Tag,
    Notifications, Messages, Bookmarks, Drafts, ReadLater, History,
    Search, Profile, Invites, TrustLevel, Settings, Topic, User
}

public sealed class AppRoute : IEquatable<AppRoute>
{
    public AppRouteKind Kind { get; init; }
    public int? Id { get; init; }
    public string? Slug { get; init; }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public int? PostNumber { get; init; }
    public string? Username { get; init; }

    public static AppRoute Latest => new() { Kind = AppRouteKind.Latest };
    public static AppRoute Top => new() { Kind = AppRouteKind.Top };
    public static AppRoute New => new() { Kind = AppRouteKind.New };
    public static AppRoute Unread => new() { Kind = AppRouteKind.Unread };
    public static AppRoute Categories => new() { Kind = AppRouteKind.Categories };
    public static AppRoute Tags => new() { Kind = AppRouteKind.Tags };
    public static AppRoute Notifications => new() { Kind = AppRouteKind.Notifications };
    public static AppRoute Messages => new() { Kind = AppRouteKind.Messages };
    public static AppRoute Bookmarks => new() { Kind = AppRouteKind.Bookmarks };
    public static AppRoute Drafts => new() { Kind = AppRouteKind.Drafts };
    public static AppRoute ReadLater => new() { Kind = AppRouteKind.ReadLater };
    public static AppRoute History => new() { Kind = AppRouteKind.History };
    public static AppRoute Search => new() { Kind = AppRouteKind.Search };
    public static AppRoute Profile => new() { Kind = AppRouteKind.Profile };
    public static AppRoute Invites => new() { Kind = AppRouteKind.Invites };
    public static AppRoute TrustLevel => new() { Kind = AppRouteKind.TrustLevel };
    public static AppRoute Settings => new() { Kind = AppRouteKind.Settings };

    public static AppRoute Category(int id, string slug, string name) => new()
    {
        Kind = AppRouteKind.Category, Id = id, Slug = slug, Name = name
    };

    public static AppRoute Tag(string name) => new()
    {
        Kind = AppRouteKind.Tag, Name = name
    };

    public static AppRoute Topic(int id, string? title = null, int? postNumber = null) => new()
    {
        Kind = AppRouteKind.Topic, Id = id, Title = title, PostNumber = postNumber
    };

    public static AppRoute User(string username) => new()
    {
        Kind = AppRouteKind.User, Username = username
    };

    public bool IsRoot => Kind is not (AppRouteKind.Category or AppRouteKind.Tag
        or AppRouteKind.Topic or AppRouteKind.User);

    public bool Equals(AppRoute? other)
    {
        if (other is null) return false;
        return Kind == other.Kind && Id == other.Id && Slug == other.Slug
               && Name == other.Name && Title == other.Title
               && PostNumber == other.PostNumber && Username == other.Username;
    }

    public override bool Equals(object? obj) => obj is AppRoute r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Kind, Id, Slug, Name, Username, PostNumber);
}

public partial class AppRouter : ObservableObject
{
    public static AppRouter Current { get; } = new();

    [ObservableProperty] private AppRoute _route = AppRoute.Latest;
    [ObservableProperty] private bool _isComposePresented;
    [ObservableProperty] private ComposeContext _composeContext = ComposeContext.NewTopic();
    [ObservableProperty] private bool _canGoBack;

    private readonly List<AppRoute> _backStack = [];

    public void SelectRoot(AppRoute route)
    {
        _backStack.Clear();
        Route = route;
        CanGoBack = false;
    }

    public void Push(AppRoute route)
    {
        _backStack.Add(Route);
        Route = route;
        CanGoBack = _backStack.Count > 0;
    }

    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        var previous = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        Route = previous;
        CanGoBack = _backStack.Count > 0;
    }

    public void OpenTopic(int id, string? title = null, int? postNumber = null)
        => Push(AppRoute.Topic(id, title, postNumber));

    public void OpenUser(string username)
    {
        var trimmed = username.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (Route.Kind == AppRouteKind.User &&
            string.Equals(Route.Username, trimmed, StringComparison.OrdinalIgnoreCase))
            return;
        Push(AppRoute.User(trimmed));
    }

    public void OpenCategory(int id, string slug, string name)
        => Push(AppRoute.Category(id, slug, name));

    public void OpenTag(string name) => Push(AppRoute.Tag(name));

    public void PresentCompose(ComposeContext? context = null)
    {
        ComposeContext = context ?? ComposeContext.NewTopic();
        IsComposePresented = true;
    }
}
