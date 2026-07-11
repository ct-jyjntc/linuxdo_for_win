using LinuxDo.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using ColorHelper = Microsoft.UI.ColorHelper;

namespace LinuxDo.Features.Connect;

public sealed partial class TrustLevelPage : Page
{
    public TrustLevelPage()
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
        LoadingRing.IsActive = true;
        ErrorText.Text = "";
        CardPanel.Visibility = Visibility.Collapsed;
        try
        {
            var data = await ConnectTrustService.FetchAsync();
            TitleText.Text = data.Title;
            CardTitle.Text = data.Title;
            BadgeText.Text = data.BadgeText;
            SubtitleText.Text = data.Subtitle;
            StatusText.Text = data.StatusText;
            StatusText.Foreground = new SolidColorBrush(
                data.IsStatusMet ? Colors.ForestGreen : Colors.Orange);
            FooterHint.Text = data.FooterHint;
            BadgeBorder.Background = new SolidColorBrush(data.BadgeKind switch
            {
                "success" => ColorHelper.FromArgb(40, 16, 124, 16),
                "danger" => ColorHelper.FromArgb(40, 196, 43, 28),
                _ => ColorHelper.FromArgb(40, 255, 140, 0)
            });
            RingsList.ItemsSource = data.Rings;
            BarsList.ItemsSource = data.Bars;
            QuotasList.ItemsSource = data.Quotas;
            VetosList.ItemsSource = data.Vetos;
            CardPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message + "\n可点击「网页版」在浏览器中查看。";
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async void OpenWeb_Click(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(ConnectTrustService.ConnectUrl);
}
