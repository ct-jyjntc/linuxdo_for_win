using Microsoft.Web.WebView2.Core;

namespace LinuxDo.Core.Utilities;

/// <summary>
/// Fit Discourse login / CF challenge pages into a narrow host by scaling the whole page.
/// CSS max-width alone cannot fix fixed-size CF widgets; zoom does.
/// </summary>
public static class WebViewPageFit
{
    // Design width Discourse/CF layouts typically need to show captcha + form.
    private const int DesignWidth = 920;
    private const double MinScale = 0.55;
    private const double MaxScale = 1.0;

    private static string BuildScript(double hostWidthHint)
    {
        // hostWidthHint from WinUI control; JS also measures clientWidth as fallback.
        var hint = hostWidthHint > 100 ? hostWidthHint.ToString("0") : "0";
        return $$"""
            (() => {
              try {
                var design = {{DesignWidth}};
                var minS = {{MinScale}};
                var maxS = {{MaxScale}};
                var hostHint = {{hint}};

                var head = document.head || document.documentElement;
                if (!head) return 'no-head';

                // Viewport so device-width matches the host when possible
                var meta = document.querySelector('meta[name="viewport"]');
                if (!meta) {
                  meta = document.createElement('meta');
                  meta.setAttribute('name', 'viewport');
                  head.appendChild(meta);
                }
                meta.setAttribute('content', 'width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no');

                var hostW = Math.max(
                  hostHint || 0,
                  document.documentElement.clientWidth || 0,
                  window.innerWidth || 0
                );
                if (hostW < 200) hostW = hostHint || 720;

                // Prefer measuring actual content overflow if present
                var scrollW = Math.max(
                  document.documentElement.scrollWidth || 0,
                  document.body ? document.body.scrollWidth : 0,
                  design
                );

                // Scale so either design width or current overflow fits
                var need = Math.max(design, Math.min(scrollW, design * 1.2));
                var scale = hostW / need;
                if (scale > maxS) scale = maxS;
                if (scale < minS) scale = minS;

                // Use zoom first (Chromium) — scales layout + hit targets together
                document.documentElement.style.zoom = String(scale);
                document.body && (document.body.style.zoom = String(scale));

                // Also set a transform fallback on html for stubborn pages
                if (scale < 0.99) {
                  document.documentElement.style.width = (100 / scale) + '%';
                } else {
                  document.documentElement.style.width = '';
                }

                var id = 'linuxdo-embed-fit';
                var style = document.getElementById(id);
                if (!style) {
                  style = document.createElement('style');
                  style.id = id;
                  head.appendChild(style);
                }
                style.textContent = `
                  html, body {
                    max-width: 100% !important;
                    overflow-x: hidden !important;
                    box-sizing: border-box !important;
                  }
                  *, *::before, *::after { box-sizing: border-box !important; }
                  img, video, canvas, svg { max-width: 100% !important; height: auto !important; }

                  /* Hide decorative right panel so form + CF dominate */
                  .login-page .login-right,
                  .login-page .login-image,
                  .login-page .login-welcome,
                  .login-page .login-carousel,
                  .login-modal .login-right,
                  aside.login-aside {
                    display: none !important;
                  }
                  .login-page .login-left,
                  .login-page .login-form,
                  .login-modal .login-left {
                    width: 100% !important;
                    max-width: 100% !important;
                    float: none !important;
                    margin: 0 auto !important;
                  }
                `;

                return 'scale=' + scale.toFixed(3) + ';host=' + Math.round(hostW) + ';scroll=' + Math.round(scrollW);
              } catch (e) {
                return 'err:' + String(e);
              }
            })()
            """;
    }

    public static async Task ApplyAsync(CoreWebView2? core, double hostWidth = 0)
    {
        if (core is null) return;
        try
        {
            // Measure host from WebView if possible via bounds later; use hint first.
            var raw = await core.ExecuteScriptAsync(BuildScript(hostWidth));
            AppLog.Network("WebViewPageFit: " + Unwrap(raw));

            // Second pass after layout (CF injects late)
            await Task.Delay(350);
            await core.ExecuteScriptAsync(BuildScript(hostWidth));
        }
        catch (Exception ex)
        {
            AppLog.Warning("webview", "PageFit: " + ex.Message);
        }
    }

    public static async Task InstallDocumentCreatedAsync(CoreWebView2? core)
    {
        if (core is null) return;
        try
        {
            // Early zoom before paint — host width unknown yet; use 720 default.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildScript(720));
        }
        catch
        {
            // ignore duplicates / older runtime
        }
    }

    private static string Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw.Trim('"');
        }
        catch
        {
            return raw.Trim().Trim('"');
        }
    }
}
