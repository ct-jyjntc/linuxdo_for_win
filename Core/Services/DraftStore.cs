using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;
namespace LinuxDo.Core.Services;

public partial class DraftStore : ObservableObject
{
    public static DraftStore Current { get; } = new();

    private const string Key = "drafts.v1";
    private readonly LocalKvStore _store = new("drafts");

    [ObservableProperty] private List<ComposeDraft> _drafts = [];
    [ObservableProperty] private bool _isSyncingServer;

    public DraftStore() => Load();

    public void Load()
    {
        try
        {
            if (_store.TryGetString(Key, out var json) && !string.IsNullOrEmpty(json))
            {
                var list = JsonSerializer.Deserialize<List<ComposeDraft>>(json, JsonFlexible.Options) ?? [];
                Drafts = list.OrderByDescending(d => d.UpdatedAt).ToList();
                return;
            }
        }
        catch { /* ignore */ }
        Drafts = [];
    }

    public void Save(ComposeDraft draft)
    {
        draft.UpdatedAt = DateTimeOffset.Now;
        var list = Drafts.ToList();
        // Prefer matching by server key when available
        if (!string.IsNullOrEmpty(draft.ServerKey))
        {
            var byKey = list.FindIndex(d => d.ServerKey == draft.ServerKey);
            if (byKey >= 0)
            {
                draft.Id = list[byKey].Id;
                list[byKey] = draft;
            }
            else
            {
                var byId = list.FindIndex(d => d.Id == draft.Id);
                if (byId >= 0) list[byId] = draft;
                else list.Insert(0, draft);
            }
        }
        else
        {
            var idx = list.FindIndex(d => d.Id == draft.Id);
            if (idx >= 0) list[idx] = draft;
            else list.Insert(0, draft);
        }
        Drafts = list.OrderByDescending(d => d.UpdatedAt).ToList();
        Persist();
    }

    public void Delete(Guid id)
    {
        Drafts = Drafts.Where(d => d.Id != id).ToList();
        Persist();
    }

    public void DeleteServerKey(string serverKey)
    {
        Drafts = Drafts.Where(d => d.ServerKey != serverKey).ToList();
        Persist();
    }

    public void Clear()
    {
        Drafts = [];
        Persist();
    }

    public async Task SyncFromServerAsync()
    {
        if (IsSyncingServer) return;
        if (!UserSessionStore.Current.IsLoggedIn) return;
        IsSyncingServer = true;
        try
        {
            await DiscourseAPI.Shared.AdoptWebCookiesAsync();
            var remote = await DiscourseAPI.Shared.FetchDraftsAsync();
            foreach (var item in remote)
                Save(ComposeDraft.FromServer(item));
        }
        catch (Exception ex)
        {
            AppLog.Warning("drafts", "Server draft sync failed: " + ex.Message);
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            IsSyncingServer = false;
        }
    }

    public async Task<ComposeDraft> SaveToServerAsync(ComposeDraft draft, string key)
    {
        var payload = new ServerDraftPayload
        {
            Reply = draft.Body,
            Title = string.IsNullOrWhiteSpace(draft.Title) ? null : draft.Title,
            CategoryId = draft.CategoryId,
            Tags = draft.Tags,
            ReplyToPostNumber = draft.ReplyToPostNumber,
            Action = draft.IsPrivateMessage == true
                ? "privateMessage"
                : draft.TopicId is null ? "createTopic" : "reply",
            Recipients = draft.PmRecipients?
                .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ArchetypeId = draft.IsPrivateMessage == true ? "private_message" : "regular"
        };

        await DiscourseAPI.Shared.PrepareWriteSessionAsync();
        var sequence = await DiscourseAPI.Shared.SaveDraftAsync(
            key, payload, draft.ServerSequence ?? 0);

        draft.ServerKey = key;
        draft.ServerSequence = sequence;
        Save(draft);
        return draft;
    }

    public async Task DeleteFromServerAsync(ComposeDraft draft)
    {
        if (string.IsNullOrEmpty(draft.ServerKey))
        {
            Delete(draft.Id);
            return;
        }
        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            await DiscourseAPI.Shared.DeleteDraftAsync(draft.ServerKey!, draft.ServerSequence ?? 0);
        }
        catch (Exception ex)
        {
            AppLog.Warning("drafts", "Delete server draft failed: " + ex.Message);
        }
        Delete(draft.Id);
    }

    public static string DraftKeyFor(ComposeContext context)
    {
        if (context.IsPrivateMessage) return DiscourseDraftKey.PrivateMessage();
        if (context.TopicId is int tid)
        {
            if (context.ReplyToPostNumber is int pn)
                return DiscourseDraftKey.Topic(tid, pn);
            return DiscourseDraftKey.Topic(tid);
        }
        return DiscourseDraftKey.NewTopic;
    }

    public static string DraftKeyFor(ComposeDraft draft)
    {
        if (!string.IsNullOrEmpty(draft.ServerKey)) return draft.ServerKey!;
        if (draft.IsPrivateMessage == true) return DiscourseDraftKey.PrivateMessage();
        if (draft.TopicId is int tid)
        {
            if (draft.ReplyToPostNumber is int pn)
                return DiscourseDraftKey.Topic(tid, pn);
            return DiscourseDraftKey.Topic(tid);
        }
        return DiscourseDraftKey.NewTopic;
    }

    private void Persist()
    {
        try
        {
            _store.SetString(Key, JsonSerializer.Serialize(Drafts, JsonFlexible.Options));
        }
        catch (Exception ex)
        {
            AppLog.Warning("drafts", ex.Message);
        }
    }
}
