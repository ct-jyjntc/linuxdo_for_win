using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml.Controls;

namespace LinuxDo.Features.Auth;

public sealed partial class SiteChallengeDialog : ContentDialog
{
    public SiteChallengeDialog()
    {
        InitializeComponent();
        MessageText.Text = SiteAccessStore.Current.ChallengeMessage;
        Loaded += async (_, _) =>
        {
            try
            {
                StatusText.Text = "正在打开验证页…";
                await ChallengeWebView.EnsureCoreWebView2Async();
                ChallengeWebView.CoreWebView2.Settings.UserAgent = CookieSessionBridge.UserAgent;
                ChallengeWebView.Source = AppSettings.Current.BaseUrl;
                ChallengeWebView.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess)
                        StatusText.Text = "若页面已通过验证，请点下方「验证完成，继续」";
                };
                StatusText.Text = "请在下方完成 Cloudflare 验证，然后点「验证完成，继续」";
            }
            catch (Exception ex)
            {
                StatusText.Text = "WebView2 初始化失败：" + ex.Message;
            }
        };
    }

    private async void Complete_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Always take deferral so we can show progress and never leave user with a dead click.
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        try
        {
            StatusText.Text = "正在同步 Cookie 并校验…（可能需要几秒）";

            // Sync cookies from THIS challenge webview into shared jar first.
            if (ChallengeWebView.CoreWebView2 is not null)
            {
                try
                {
                    var cookies = await ChallengeWebView.CoreWebView2.CookieManager.GetCookiesAsync(
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
                        catch { /* skip bad cookie */ }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("challenge", "cookie sync: " + ex.Message);
                }
            }

            // Also pull from the app bridge WebView if available.
            try
            {
                await CookieSessionBridge.SyncWebViewCookiesToHttpAsync(AppSettings.Current.BaseUrl, force: true);
            }
            catch { /* best effort */ }

            StatusText.Text = "正在检测是否已通过验证…";
            var ok = false;
            try
            {
                ok = await SiteAccessStore.Current.CompleteChallengeAsync(AppSettings.Current.BaseUrl);
            }
            catch (Exception ex)
            {
                AppLog.Warning("challenge", "complete: " + ex.Message);
                // Don't surface raw JsonException to the user when CF already passed.
                SiteAccessStore.Current.LastError = "检测异常，将强制继续";
            }

            if (ok)
            {
                StatusText.Text = "验证成功，正在刷新…";
            }
            else
            {
                // Even if JSON parse of the probe failed, cookies are often already good —
                // force-continue so the user isn't stuck on a scary error string.
                StatusText.Text = "正在完成验证并刷新…";
                try
                {
                    await SiteAccessStore.Current.ForceContinueAsync(AppSettings.Current.BaseUrl);
                    StatusText.Text = "已继续，正在刷新列表…";
                }
                catch (Exception ex)
                {
                    AppLog.Warning("challenge", "force: " + ex.Message);
                    SiteAccessStore.Current.DismissWithoutClearing();
                    StatusText.Text = "已关闭验证；若列表仍失败请再点刷新。";
                }
            }

            // Dialog closes after deferral completes (Primary accepted).
            if (SiteAccessStore.Current.NeedsChallenge)
                SiteAccessStore.Current.DismissWithoutClearing();
        }
        catch (Exception ex)
        {
            // Don't cancel by default — cancel only keeps user stuck if they can't proceed.
            StatusText.Text = "处理异常：" + ex.Message + "（已尝试关闭，请再点刷新）";
            try { SiteAccessStore.Current.DismissWithoutClearing(); } catch { /* ignore */ }
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void Dismiss_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SiteAccessStore.Current.DismissWithoutClearing();
    }
}
