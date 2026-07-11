using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinuxDo.Features.Compose;

public sealed class DraftItemVm
{
    public ComposeDraft Draft { get; init; } = new();
    public string Title => string.IsNullOrEmpty(Draft.Title)
        ? (Draft.IsReply ? "回复草稿" : Draft.IsPrivateMessage == true ? "私信草稿" : "新主题草稿")
        : Draft.Title;
    public string Subtitle =>
        $"{(Draft.Body.Split('\n').FirstOrDefault() ?? "")} · {RelativeDate.Describe(Draft.UpdatedAt)}";
    public string Badge => Draft.IsServerBacked ? "☁ 已云同步" : "本地";
}

public sealed partial class DraftsPage : Page
{
    private ListKeyboardNav? _nav;

    public DraftsPage()
    {
        InitializeComponent();
        _nav = ListKeyboardNav.Attach(DraftList, item =>
        {
            if (item is DraftItemVm d)
                AppRouter.Current.PresentCompose(ComposeContext.FromDraft(d.Draft));
        });
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) =>
        {
            _nav?.Dispose();
            AppEvents.Refresh -= OnRefresh;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload();
        if (UserSessionStore.Current.IsLoggedIn)
        {
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            try
            {
                await DraftStore.Current.SyncFromServerAsync();
            }
            catch
            {
                // keep local drafts
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
            Reload();
        }
        else
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnRefresh()
    {
        if (UserSessionStore.Current.IsLoggedIn)
        {
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            try { await DraftStore.Current.SyncFromServerAsync(); }
            catch { /* ignore */ }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
        Reload();
    }

    private void Reload()
    {
        var items = DraftStore.Current.Drafts.Select(d => new DraftItemVm { Draft = d }).ToList();
        DraftList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private void DraftList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DraftItemVm item)
            AppRouter.Current.PresentCompose(ComposeContext.FromDraft(item.Draft));
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        try
        {
            await DraftStore.Current.SyncFromServerAsync();
            Reload();
        }
        catch (Exception ex)
        {
            EmptyText.Text = "同步失败：" + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DraftItemVm item) return;
        try
        {
            await DraftStore.Current.DeleteFromServerAsync(item.Draft);
        }
        catch
        {
            DraftStore.Current.Delete(item.Draft.Id);
        }
        Reload();
    }
}
