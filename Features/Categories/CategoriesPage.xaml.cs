using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace LinuxDo.Features.Categories;

public sealed class CategoryItemVm
{
    public DiscourseCategory Category { get; init; } = new();
    public string Name => Category.Name;
    public string? Description => Category.Description;
    public string CountText => $"主题 {Category.TopicCount ?? 0}";
    public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
}

public sealed partial class CategoriesPage : Page
{
    public CategoriesPage()
    {
        InitializeComponent();
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) => AppEvents.Refresh -= OnRefresh;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadAsync(force: CategoryList.ItemsSource is null);
    }

    private async void OnRefresh() => await LoadAsync(force: true);

    private async Task LoadAsync(bool force = false)
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            await CategoryStore.Current.LoadAsync(force: force);
            var items = CategoryStore.Current.TopLevelCategories
                .Select(c => new CategoryItemVm
                {
                    Category = c,
                    ColorBrush = new SolidColorBrush(ParseColor(c.Color))
                }).ToList();
            CategoryList.ItemsSource = items;
            if (items.Count == 0)
            {
                EmptyText.Text = string.IsNullOrEmpty(CategoryStore.Current.ErrorMessage)
                    ? "暂无分类"
                    : CategoryStore.Current.ErrorMessage!;
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

    private void CategoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CategoryItemVm item)
        {
            AppRouter.Current.OpenCategory(
                item.Category.Id,
                item.Category.Slug ?? item.Category.Id.ToString(),
                item.Category.Name);
        }
    }

    private static Color ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.Gray;
        var cleaned = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length != 6) return Colors.Gray;
        try
        {
            var r = byte.Parse(cleaned[..2], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(cleaned[2..4], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(cleaned[4..6], System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb(255, r, g, b);
        }
        catch { return Colors.Gray; }
    }
}
