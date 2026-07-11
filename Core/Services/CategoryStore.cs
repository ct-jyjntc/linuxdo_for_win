using CommunityToolkit.Mvvm.ComponentModel;
using LinuxDo.Core.Models;

namespace LinuxDo.Core.Services;

public partial class CategoryStore : ObservableObject
{
    public static CategoryStore Current { get; } = new();

    [ObservableProperty] private List<DiscourseCategory> _categories = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    private readonly Dictionary<int, DiscourseUser> _usersById = new();
    private Task? _loadTask;

    public IReadOnlyList<DiscourseCategory> TopLevelCategories =>
        Categories.Where(c => c.ParentCategoryId is null).ToList();

    public DiscourseCategory? Category(int? id)
        => id is null ? null : Categories.FirstOrDefault(c => c.Id == id);

    public async Task LoadAsync(bool force = false)
    {
        if (!force && Categories.Count > 0) return;
        if (_loadTask is not null && !force)
        {
            await _loadTask;
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        var task = LoadInternalAsync();
        _loadTask = task;
        await task;
    }

    private async Task LoadInternalAsync()
    {
        try
        {
            Categories = await DiscourseAPI.Shared.FetchCategoriesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            IsLoading = false;
            _loadTask = null;
        }
    }

    public void CacheUsers(IEnumerable<DiscourseUser>? users)
    {
        if (users is null) return;
        foreach (var u in users) _usersById[u.Id] = u;
    }

    public DiscourseUser? User(int? id)
        => id is null ? null : _usersById.GetValueOrDefault(id.Value);
}
