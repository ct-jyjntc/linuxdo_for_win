using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;
namespace LinuxDo.Core.Services;

public partial class ReadLaterStore : ObservableObject
{
    public static ReadLaterStore Current { get; } = new();

    private const string Key = "readlater.topics.v1";
    private readonly LocalKvStore _store = new("readlater");

    [ObservableProperty] private List<LocalTopicItem> _items = [];

    public int Count => Items.Count;

    public ReadLaterStore() => Load();

    public void Load()
    {
        try
        {
            if (_store.TryGetString(Key, out var json) && !string.IsNullOrEmpty(json))
            {
                var list = JsonSerializer.Deserialize<List<LocalTopicItem>>(json, JsonFlexible.Options) ?? [];
                Items = list.OrderByDescending(i => i.SavedAt ?? i.LastVisitedAt).ToList();
                return;
            }
        }
        catch { /* ignore */ }
        Items = [];
    }

    public bool Contains(int topicId) => Items.Any(i => i.Id == topicId);

    public void Add(int topicId, string title, string? slug = null, int? categoryId = null,
        List<string>? tags = null, int? postsCount = null, int? views = null, int? likeCount = null,
        string? author = null)
    {
        var list = Items.Where(i => i.Id != topicId).ToList();
        list.Insert(0, new LocalTopicItem
        {
            Id = topicId,
            Title = title,
            Slug = slug,
            CategoryId = categoryId,
            Tags = tags ?? [],
            AuthorUsername = author,
            PostsCount = postsCount,
            Views = views,
            LikeCount = likeCount,
            LastVisitedAt = DateTimeOffset.Now,
            SavedAt = DateTimeOffset.Now
        });
        Items = list;
        Persist();
        OnPropertyChanged(nameof(Count));
    }

    public void Remove(int topicId)
    {
        Items = Items.Where(i => i.Id != topicId).ToList();
        Persist();
        OnPropertyChanged(nameof(Count));
    }

    public bool Toggle(int topicId, string title, string? slug = null, int? categoryId = null,
        List<string>? tags = null, int? postsCount = null, int? views = null, int? likeCount = null,
        string? author = null)
    {
        if (Contains(topicId))
        {
            Remove(topicId);
            return false;
        }
        Add(topicId, title, slug, categoryId, tags, postsCount, views, likeCount, author);
        return true;
    }

    public void Clear()
    {
        Items = [];
        Persist();
        OnPropertyChanged(nameof(Count));
    }

    private void Persist()
    {
        try
        {
            _store.SetString(Key, JsonSerializer.Serialize(Items, JsonFlexible.Options));
        }
        catch (Exception ex)
        {
            AppLog.Warning("readlater", ex.Message);
        }
    }
}
