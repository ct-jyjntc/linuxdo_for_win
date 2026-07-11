using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Library;

public sealed class LocalItemVm
{
    public LocalTopicItem Item { get; init; } = new();
    public string Title => Item.Title;
    public string Subtitle =>
        $"{(Item.AuthorUsername is not null ? "@" + Item.AuthorUsername + " · " : "")}" +
        RelativeDate.Describe(Item.SavedAt ?? Item.LastVisitedAt);
}

public sealed partial class LocalListsPage : Page
{
    private bool _isReadLater;

    private ListKeyboardNav? _nav;

    public LocalListsPage()
    {
        InitializeComponent();
        _nav = ListKeyboardNav.Attach(ItemList, item =>
        {
            if (item is LocalItemVm li)
                AppRouter.Current.OpenTopic(li.Item.Id, li.Item.Title);
        });
        Unloaded += (_, _) => _nav?.Dispose();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isReadLater = e.Parameter is string s && s == "readlater"
                       || e.Parameter is AppRoute { Kind: AppRouteKind.ReadLater };
        TitleText.Text = _isReadLater ? "稍后阅读" : "浏览历史";
        Reload();
    }

    private void Reload()
    {
        var source = _isReadLater
            ? ReadLaterStore.Current.Items
            : ReadingHistoryStore.Current.Items;
        var items = source.Select(i => new LocalItemVm { Item = i }).ToList();
        ItemList.ItemsSource = items;
        EmptyText.Text = _isReadLater ? "暂无稍后阅读" : "暂无浏览历史";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LocalItemVm item)
            AppRouter.Current.OpenTopic(item.Item.Id, item.Item.Title);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "确认清空",
            Content = _isReadLater ? "清空全部稍后阅读？" : "清空全部浏览历史？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (_isReadLater) ReadLaterStore.Current.Clear();
            else ReadingHistoryStore.Current.Clear();
            Reload();
        }
    }
}
