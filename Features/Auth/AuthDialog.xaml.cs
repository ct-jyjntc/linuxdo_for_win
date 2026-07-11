using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinuxDo.Features.Auth;

public sealed partial class AuthDialog : ContentDialog
{
    private bool _navigated;

    public AuthDialog()
    {
        InitializeComponent();
        Loaded += AuthDialog_Loaded;
        ModePivot.SelectionChanged += ModePivot_SelectionChanged;
    }

    private async void AuthDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_navigated) return;
        _navigated = true;
        try
        {
            await LoginWebView.EnsureCoreWebView2Async();
            LoginWebView.CoreWebView2.Settings.UserAgent = CookieSessionBridge.UserAgent;
            LoginWebView.Source = AppSettings.Current.BaseUrl;
            LoginWebView.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess) return;
                // Probe session after navigation settles
                try
                {
                    await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
                    if (CookieSessionBridge.HasSessionCookie(AppSettings.Current.BaseUrl))
                    {
                        StatusText.Text = "检测到会话 Cookie，正在同步…";
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
        }
        catch (Exception ex)
        {
            ErrorText.Text = "WebView2 初始化失败：" + ex.Message;
        }
    }

    private void ModePivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var advanced = ModePivot.SelectedIndex == 1;
        WebPane.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPane.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DetectLogin_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            StatusText.Text = "正在检测登录状态…";
            // Sync cookies from this dialog's webview into shared jar
            if (LoginWebView.CoreWebView2 is not null)
            {
                var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(
                    AppSettings.Current.BaseUrl.AbsoluteUri);
                foreach (var c in cookies)
                {
                    try
                    {
                        CookieSessionBridge.CookieJar.Add(AppSettings.Current.BaseUrl,
                            new System.Net.Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain ?? AppSettings.Current.BaseUrl.Host)
                            {
                                Secure = c.IsSecure,
                                HttpOnly = c.IsHttpOnly
                            });
                    }
                    catch { /* skip */ }
                }
            }

            await UserSessionStore.Current.CompleteWebLoginAsync();
            if (UserSessionStore.Current.IsLoggedIn)
            {
                StatusText.Text = $"已登录：{UserSessionStore.Current.Username}";
            }
            else
            {
                args.Cancel = true;
                ErrorText.Text = UserSessionStore.Current.AuthError ?? "未检测到登录状态";
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
