using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinuxDo.Features.Topic;

public sealed partial class UserHoverCardControl : UserControl
{
    private string _username = "";
    private UserProfile? _profile;

    public event Action<string>? OpenProfile;
    public event Action<string>? OpenMessage;

    public UserHoverCardControl()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(string username)
    {
        _username = username;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        CardBody.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        BioText.Text = "";
        try
        {
            _profile = await DiscourseAPI.Shared.FetchUserCardAsync(username);
            DisplayNameText.Text = _profile.DisplayName;
            UsernameText.Text = "@" + _profile.Username;
            TitleText.Text = _profile.Title ?? "";
            TlText.Text = _profile.TrustLevel is int tl ? $"TL{tl}" : "";
            TlBadge.Visibility = _profile.TrustLevel is not null ? Visibility.Visible : Visibility.Collapsed;
            BioText.Text = HtmlText.PlainText(_profile.BioExcerpt ?? _profile.BioRaw ?? "");
            FollowersText.Text = (_profile.TotalFollowers ?? 0).ToString();
            FollowingText.Text = (_profile.TotalFollowing ?? 0).ToString();
            BadgesText.Text = (_profile.BadgeCount ?? 0).ToString();
            StatsPanel.Visibility = Visibility.Visible;
            var avatar = _profile.AvatarUrl(AppSettings.Current.BaseUrl, 120);
            AvatarImage.SourceUrl = avatar?.AbsoluteUri;
            CardBody.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
        => OpenProfile?.Invoke(_username);

    private void Message_Click(object sender, RoutedEventArgs e)
        => OpenMessage?.Invoke(_username);
}
