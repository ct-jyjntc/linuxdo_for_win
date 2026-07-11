using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LinuxDo.Features.Topic;

public sealed class ReactionUserRow
{
    public string DisplayName { get; init; } = "";
    public string Username { get; init; } = "";
    public BitmapImage? Avatar { get; init; }
}

public sealed partial class ReactionUsersDialog : ContentDialog
{
    private List<ReactionUsersGroup> _groups = [];
    private string? _selected;

    public event Action<string>? OpenUser;

    public ReactionUsersDialog(int postId)
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync(postId);
    }

    private async Task LoadAsync(int postId)
    {
        LoadingRing.IsActive = true;
        try
        {
            _groups = await DiscourseAPI.Shared.FetchReactionUsersAsync(postId);
            BuildChips();
            ApplyFilter(null);
            if (_groups.Count == 0)
            {
                EmptyText.Text = "暂无反应用户";
                EmptyText.Visibility = Visibility.Visible;
            }
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

    private void BuildChips()
    {
        ChipPanel.Children.Clear();
        var all = new Button { Content = "全部", Tag = "" };
        all.Click += Chip_Click;
        ChipPanel.Children.Add(all);
        foreach (var g in _groups.Where(g => g.Count > 0 || g.Users.Count > 0))
        {
            var btn = new Button
            {
                Content = $"{CommonReactions.Display(g.Reaction)} {g.Count}",
                Tag = g.Reaction
            };
            btn.Click += Chip_Click;
            ChipPanel.Children.Add(btn);
        }
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var tag = fe.Tag as string;
            ApplyFilter(string.IsNullOrEmpty(tag) ? null : tag);
        }
    }

    private void ApplyFilter(string? reaction)
    {
        _selected = reaction;
        var baseUrl = AppSettings.Current.BaseUrl;
        IEnumerable<FollowUser> users;
        if (reaction is null)
        {
            var seen = new HashSet<int>();
            var list = new List<FollowUser>();
            foreach (var g in _groups)
            foreach (var u in g.Users)
                if (seen.Add(u.Id)) list.Add(u);
            users = list;
        }
        else
        {
            users = _groups.FirstOrDefault(g => g.Reaction == reaction)?.Users ?? [];
        }

        UserList.ItemsSource = users.Select(u => new ReactionUserRow
        {
            DisplayName = u.DisplayName,
            Username = "@" + u.Username,
            Avatar = u.AvatarUrl(baseUrl) is { } uri ? new BitmapImage(uri) : null
        }).ToList();
        EmptyText.Visibility = !users.Any() ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "暂无用户";
    }

    private void UserList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReactionUserRow row)
        {
            var name = row.Username.TrimStart('@');
            OpenUser?.Invoke(name);
            Hide();
        }
    }
}
