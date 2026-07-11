using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace LinuxDo.Features.Auth;

/// <summary>
/// Login uses the **same** BridgeWebView as CF challenge / API (mac parity).
/// </summary>
public sealed partial class AuthDialog : ContentDialog
{
    private bool _navigated;
    private WebView2? _webView;
    private Panel? _originalParent;
    private int _originalIndex = -1;
    private double _savedWidth, _savedHeight, _savedOpacity = 1;
    private bool _savedHitTest;
    private HorizontalAlignment _savedHAlign;
    private VerticalAlignment _savedVAlign;
    private Thickness _savedMargin;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navHandler;

    public AuthDialog()
    {
        InitializeComponent();
        Loaded += AuthDialog_Loaded;
        Closed += AuthDialog_Closed;
        ModePivot.SelectionChanged += ModePivot_SelectionChanged;
    }

    private void SizeDialogToWindow()
    {
        // Moderate dialog; page layout is constrained via WebViewPageFit CSS injection.
        try
        {
            double w = 780;
            double h = 720;
            if (App.Window?.Bounds is { Width: > 0, Height: > 0 } b)
            {
                w = Math.Clamp(b.Width * 0.72, 700, 900);
                h = Math.Clamp(b.Height * 0.85, 600, 900);
            }
            MinWidth = w;
            MaxWidth = 960;
            Width = w;
            RootGrid.MinWidth = w - 40;
            RootGrid.MinHeight = Math.Max(560, h - 120);
            WebHost.MinHeight = Math.Max(420, h - 260);
            WebHost.ClearValue(FrameworkElement.WidthProperty);
            WebHost.ClearValue(FrameworkElement.MinWidthProperty);
        }
        catch
        {
            MinWidth = 760;
            MaxWidth = 960;
            Width = 780;
            RootGrid.MinHeight = 600;
            WebHost.MinHeight = 460;
        }
    }

    private async void AuthDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_navigated) return;
        _navigated = true;
        SizeDialogToWindow();
        try
        {
            _webView = WebViewAPIClient.Shared.GetWebView();
            if (_webView is null)
            {
                ErrorText.Text = "内部浏览器未初始化，请重启应用后再试。";
                return;
            }

            WebViewAPIClient.Shared.SetChallengeUiActive(true);
            ReparentIntoHost(_webView);
            await WebViewAPIClient.Shared.EnsureInitializedAsync();

            var core = _webView.CoreWebView2;
            if (core is null)
            {
                ErrorText.Text = "WebView2 核心未就绪";
                return;
            }

            CookieSessionBridge.PrimeUserAgent();
            await CookieSessionBridge.PrimeUserAgentFromWebViewAsync(core);
            await WebViewPageFit.InstallDocumentCreatedAsync(core);

            _navHandler = async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    StatusText.Text = "页面加载失败，可点「刷新页面」重试";
                    return;
                }
                var hostW = WebHost.ActualWidth > 100 ? WebHost.ActualWidth : ActualWidth;
                await WebViewPageFit.ApplyAsync(core, hostW);
                // CF captcha mounts late — second scale pass on UI timer.
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(800);
                timer.IsRepeating = false;
                timer.Tick += async (_, __) =>
                {
                    try { await WebViewPageFit.ApplyAsync(core, hostW); } catch { /* ignore */ }
                    try { timer.Stop(); } catch { /* ignore */ }
                };
                timer.Start();
                StatusText.Text = "页面已加载，请登录后点「我已登录，立即检测」";
                try
                {
                    await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
                    if (CookieSessionBridge.HasLoginCookie(AppSettings.Current.BaseUrl))
                    {
                        StatusText.Text = "检测到登录 Cookie，正在同步…";
                        await UserSessionStore.Current.CompleteWebLoginAsync();
                        if (UserSessionStore.Current.IsLoggedIn)
                        {
                            StatusText.Text = $"已登录：{UserSessionStore.Current.Username}";
                            Hide();
                        }
                    }
                }
                catch
                {
                    // ignore auto probe errors
                }
            };
            core.NavigationCompleted += _navHandler;

            // Always navigate to login entry so the host is not left on a blank/off-screen frame.
            StatusText.Text = "正在打开登录页…";
            NavigateToLoginHome(force: true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "WebView2 初始化失败：" + ex.Message;
        }
    }

    private void AuthDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        try
        {
            if (_webView?.CoreWebView2 is not null && _navHandler is not null)
                _webView.CoreWebView2.NavigationCompleted -= _navHandler;
        }
        catch { /* ignore */ }
        _navHandler = null;
        RestoreWebViewParent();
        WebViewAPIClient.Shared.SetChallengeUiActive(false);
    }

    private void ReparentIntoHost(WebView2 webView)
    {
        if (webView.Parent is Panel parent)
        {
            _originalParent = parent;
            _originalIndex = parent.Children.IndexOf(webView);
            parent.Children.Remove(webView);
        }
        else if (webView.Parent is Border border)
        {
            // Already in another dialog host — detach carefully.
            border.Child = null;
        }

        _savedWidth = webView.Width;
        _savedHeight = webView.Height;
        _savedOpacity = webView.Opacity;
        _savedHitTest = webView.IsHitTestVisible;
        _savedHAlign = webView.HorizontalAlignment;
        _savedVAlign = webView.VerticalAlignment;
        _savedMargin = webView.Margin;

        // CRITICAL: clear off-screen Margin from the hidden bridge (-4000,-4000).
        // Leaving it makes the login pane appear completely blank.
        webView.Margin = new Thickness(0);
        webView.Width = double.NaN;
        webView.Height = double.NaN;
        webView.MinWidth = 0;
        webView.MinHeight = 0;
        webView.Opacity = 1;
        webView.IsHitTestVisible = true;
        webView.HorizontalAlignment = HorizontalAlignment.Stretch;
        webView.VerticalAlignment = VerticalAlignment.Stretch;
        webView.Visibility = Visibility.Visible;
        WebHost.Child = webView;
    }

    private void RestoreWebViewParent()
    {
        if (_webView is null) return;
        try { WebHost.Child = null; } catch { /* ignore */ }

        _webView.Width = _savedWidth > 100 ? _savedWidth : 960;
        _webView.Height = _savedHeight > 100 ? _savedHeight : 700;
        _webView.Opacity = 0.01;
        _webView.IsHitTestVisible = false;
        _webView.HorizontalAlignment = _savedHAlign;
        _webView.VerticalAlignment = _savedVAlign;
        // Park off-screen again for background bridge use.
        _webView.Margin = new Thickness(-4000, -4000, 0, 0);
        _webView.Visibility = Visibility.Visible;

        if (_originalParent is not null)
        {
            try
            {
                if (_originalIndex >= 0 && _originalIndex <= _originalParent.Children.Count)
                    _originalParent.Children.Insert(_originalIndex, _webView);
                else
                    _originalParent.Children.Add(_webView);
            }
            catch
            {
                try { _originalParent.Children.Add(_webView); } catch { /* ignore */ }
            }
        }

        WebViewAPIClient.Shared.Attach(_webView);
        _webView = null;
        _originalParent = null;
    }

    private void ModePivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var advanced = ModePivot.SelectedIndex == 1;
        WebHost.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        WebToolbar.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPane.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigateToLoginHome(bool force)
    {
        if (_webView is null) return;
        var baseUrl = AppSettings.Current.BaseUrl;
        // Discourse login entry
        var login = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/login");
        try
        {
            if (!force &&
                _webView.Source is not null &&
                string.Equals(_webView.Source.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase) &&
                (_webView.Source.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                 _webView.Source.AbsolutePath.Contains("session", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }
        catch { /* navigate */ }

        StatusText.Text = "正在打开登录页…";
        try
        {
            if (_webView.CoreWebView2 is not null)
                _webView.CoreWebView2.Navigate(login.AbsoluteUri);
            else
                _webView.Source = login;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "导航失败：" + ex.Message;
            try { _webView.Source = baseUrl; } catch { /* ignore */ }
        }
    }

    private void RefreshPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView?.CoreWebView2 is not null)
            {
                StatusText.Text = "正在刷新…";
                ErrorText.Text = "";
                // Clear margin again in case layout got stuck.
                _webView.Margin = new Thickness(0);
                _webView.Opacity = 1;
                _webView.Visibility = Visibility.Visible;
                _webView.CoreWebView2.Reload();
            }
            else
            {
                NavigateToLoginHome(force: true);
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = "刷新失败：" + ex.Message;
            NavigateToLoginHome(force: true);
        }
    }

    private async void FitScale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return;
            var hostW = WebHost.ActualWidth > 100 ? WebHost.ActualWidth : ActualWidth;
            await WebViewPageFit.ApplyAsync(_webView.CoreWebView2, hostW);
            StatusText.Text = "已按窗口缩放页面";
        }
        catch (Exception ex)
        {
            ErrorText.Text = "缩放失败：" + ex.Message;
        }
    }

    private void GoLogin_Click(object sender, RoutedEventArgs e)
        => NavigateToLoginHome(force: true);

    private void GoBack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView?.CoreWebView2?.CanGoBack == true)
                _webView.CoreWebView2.GoBack();
            else
                StatusText.Text = "没有上一页";
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void DetectLogin_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            StatusText.Text = "正在检测登录状态…";
            ErrorText.Text = "";
            await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
            try { await WebViewAPIClient.Shared.AdoptVerifiedAsync(AppSettings.Current.BaseUrl); }
            catch { /* best effort */ }

            await UserSessionStore.Current.CompleteWebLoginAsync();
            if (UserSessionStore.Current.IsLoggedIn)
            {
                StatusText.Text = $"已登录：{UserSessionStore.Current.Username}";
            }
            else
            {
                args.Cancel = true;
                ErrorText.Text = UserSessionStore.Current.AuthError
                    ?? (CookieSessionBridge.HasLoginCookie(AppSettings.Current.BaseUrl)
                        ? "有 Cookie 但读取用户失败，请刷新后重试"
                        : "未检测到登录 Cookie，请先在页面中登录");
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ErrorText.Text = ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void BrowserAuth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在打开浏览器授权页…";
            ErrorText.Text = "";
            await UserSessionStore.Current.BeginUserApiAuthAsync();
            StatusText.Text = "请在浏览器完成授权，完成后会通过 linuxdo://auth 回调自动登录";
            if (!string.IsNullOrEmpty(UserSessionStore.Current.AuthError))
                ErrorText.Text = UserSessionStore.Current.AuthError;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void ManualLogin_Click(object sender, RoutedEventArgs e)
    {
        var user = ManualUsername.Text?.Trim() ?? "";
        var key = ManualKey.Password?.Trim() ?? "";
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(key))
        {
            ErrorText.Text = "请填写用户名和 API Key";
            return;
        }
        await UserSessionStore.Current.LoginWithManualKeyAsync(key, user);
        if (UserSessionStore.Current.IsLoggedIn)
            Hide();
        else
            ErrorText.Text = UserSessionStore.Current.AuthError ?? "登录失败";
    }
}
