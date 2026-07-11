using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Tags;

public sealed class TagItemVm
{
    public DiscourseTag Tag { get; init; } = new();
    public string Name => Tag.Name;
    public string CountText => $"{Tag.Count ?? 0} 主题";
}

public sealed partial class TagsPage : Page
{
    private bool _loadedOnce;

    public TagsPage()
    {
        InitializeComponent();
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) => AppEvents.Refresh -= OnRefresh;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_loadedOnce || TagGrid.ItemsSource is null)
            await LoadAsync();
    }

    private async void OnRefresh() => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var tags = await DiscourseAPI.Shared.FetchTagsAsync();
            var items = tags
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .OrderByDescending(t => t.Count ?? 0)
                .Select(t => new TagItemVm { Tag = t })
                .ToList();
            TagGrid.ItemsSource = items;
            _loadedOnce = true;
            if (items.Count == 0)
            {
                EmptyText.Text = "暂无标签";
                EmptyText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            EmptyText.Text = "加载失败：" + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void TagGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TagItemVm item)
            AppRouter.Current.OpenTag(item.Name);
    }
}
