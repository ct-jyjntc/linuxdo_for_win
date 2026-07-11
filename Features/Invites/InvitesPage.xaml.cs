using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace LinuxDo.Features.Invites;

public sealed class InviteItemVm
{
    public InviteLink Invite { get; init; } = new();
    public string Link => string.IsNullOrEmpty(Invite.Link) ? "(无链接)" : Invite.Link;
    public string Meta
    {
        get
        {
            var parts = new List<string> { Invite.RemainingText };
            if (Invite.Expired == true) parts.Add("已过期");
            if (Invite.ExpiresDate is not null) parts.Add("到期 " + RelativeDate.Describe(Invite.ExpiresDate));
            if (Invite.CreatedDate is not null) parts.Add("创建 " + RelativeDate.Describe(Invite.CreatedDate));
            return string.Join(" · ", parts);
        }
    }
    public string Description => Invite.Description ?? "";
}

public sealed partial class InvitesPage : Page
{
    public InvitesPage()
    {
        InitializeComponent();
        AppEvents.Refresh += OnRefresh;
        Unloaded += (_, _) => AppEvents.Refresh -= OnRefresh;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!UserSessionStore.Current.RequireLogin()) return;
        await LoadAsync();
    }

    private async void OnRefresh() => await LoadAsync();

    private async Task LoadAsync()
    {
        var username = UserSessionStore.Current.Username;
        if (string.IsNullOrEmpty(username)) return;
        LoadingRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var list = await DiscourseAPI.Shared.FetchPendingInvitesAsync(username);
            var items = list.Select(i => new InviteItemVm { Invite = i }).ToList();
            InviteList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;

        var maxBox = new NumberBox
        {
            Header = "最大使用次数",
            Value = 5,
            Minimum = 1,
            Maximum = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var descBox = new TextBox { Header = "备注（可选）", PlaceholderText = "例如：给朋友" };
        var daysBox = new NumberBox
        {
            Header = "有效天数（0=默认）",
            Value = 0,
            Minimum = 0,
            Maximum = 365,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var panel = new StackPanel { Spacing = 10, Children = { maxBox, daysBox, descBox } };
        var dialog = new ContentDialog
        {
            Title = "创建邀请链接",
            Content = panel,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            DateTimeOffset? expires = daysBox.Value > 0
                ? DateTimeOffset.UtcNow.AddDays(daysBox.Value)
                : null;
            var invite = await DiscourseAPI.Shared.CreateInviteLinkAsync(
                maxRedemptions: (int)maxBox.Value,
                expiresAt: expires,
                description: descBox.Text);
            StatusBar.Message = string.IsNullOrEmpty(invite.Link)
                ? "邀请已创建"
                : "已创建：" + invite.Link;
            StatusBar.IsOpen = true;
            if (!string.IsNullOrEmpty(invite.Link))
            {
                var dp = new DataPackage();
                dp.SetText(invite.Link);
                Clipboard.SetContent(dp);
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
            APIError.PostIfChallenge(ex);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InviteItemVm item && !string.IsNullOrEmpty(item.Invite.Link))
        {
            var dp = new DataPackage();
            dp.SetText(item.Invite.Link);
            Clipboard.SetContent(dp);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = "链接已复制";
            StatusBar.IsOpen = true;
        }
    }

    private async void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSessionStore.Current.RequireLogin()) return;
        var username = UserSessionStore.Current.Username;
        if (string.IsNullOrEmpty(username)) return;
        var url = new Uri(AppSettings.Current.BaseUrl, $"u/{username}/invited/pending");
        await Launcher.LaunchUriAsync(url);
    }
}
