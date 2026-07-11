using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;
namespace LinuxDo.Core.Services;

public partial class ReadingHistoryStore : ObservableObject
{
    public static ReadingHistoryStore Current { get; } = new();

    private const string Key = "history.topics.v1";
    private const int MaxItems = 200;
    private readonly LocalKvStore _store = new("history");

    [ObservableProperty] private List<LocalTopicItem> _items = [];

    public ReadingHistoryStore() => Load();

    public void Load()
    {
        try
        {
            if (_store.TryGetString(Key, out var json) && !string.IsNullOrEmpty(json))
            {
                var list = JsonSerializer.Deserialize<List<LocalTopicItem>>(json, JsonFlexible.Options) ?? [];
                Items = list.OrderByDescending(i => i.LastVisitedAt).ToList();
                return;
            }
        }
        catch { /* ignore */ }
        Items = [];
    }

    public void Record(int topicId, string title, string? slug = null, int? categoryId = null,
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
            LastVisitedAt = DateTimeOffset.Now
        });
        if (list.Count > MaxItems) list = list.Take(MaxItems).ToList();
        Items = list;
        Persist();
    }

    public void Remove(int id)
    {
        Items = Items.Where(i => i.Id != id).ToList();
        Persist();
    }

    public void Clear()
    {
        Items = [];
        Persist();
    }

    private void Persist()
    {
        try
        {
            _store.SetString(Key, JsonSerializer.Serialize(Items, JsonFlexible.Options));
        }
        catch (Exception ex)
        {
            AppLog.Warning("history", ex.Message);
        }
    }
}
