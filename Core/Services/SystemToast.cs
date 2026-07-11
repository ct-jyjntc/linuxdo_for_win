using LinuxDo.Core.Utilities;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LinuxDo.Core.Services;

/// <summary>Windows toast / in-app status helpers.</summary>
public static class SystemToast
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked;
            manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("toast", "Register failed: " + ex.Message);
        }
    }

    public static void Show(string title, string body, int? topicId = null, int? postNumber = null)
    {
        if (!AppSettings.Current.SystemNotificationBanners) return;
        try
        {
            EnsureRegistered();
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);
            if (topicId is int tid)
            {
                builder.AddArgument("action", "openTopic");
                builder.AddArgument("topicId", tid.ToString());
                if (postNumber is int pn)
                    builder.AddArgument("postNumber", pn.ToString());
            }
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            AppLog.Warning("toast", ex.Message);
        }
    }

    public static void ShowFromNotification(Dictionary<string, object?> data)
    {
        var fancyTitle = data.GetValueOrDefault("fancy_title") as string;
        var topicTitle = NestedString(data, "data", "topic_title")
                         ?? data.GetValueOrDefault("topic_title") as string;
        var displayUser = NestedString(data, "data", "display_username")
                          ?? data.GetValueOrDefault("display_username") as string
                          ?? data.GetValueOrDefault("username") as string;
        var type = data.GetValueOrDefault("notification_type") switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var n) => n,
            _ => 0
        };
        var typeLabel = type switch
        {
            1 => "提到了你",
            2 => "回复了你",
            3 => "引用了你",
            5 => "点赞了",
            6 => "私信",
            9 => "邀请",
            12 => "徽章",
            _ => "新通知"
        };
        var title = displayUser is not null ? $"@{displayUser} {typeLabel}" : typeLabel;
        var body = fancyTitle ?? topicTitle ?? "打开 LinuxDo 查看详情";
        int? topicId = data.GetValueOrDefault("topic_id") switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
        int? postNumber = data.GetValueOrDefault("post_number") switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var n) => n,
            _ => null
        };
        Show(title, body, topicId, postNumber);
    }

    private static string? NestedString(Dictionary<string, object?> data, string key, string inner)
    {
        if (data.GetValueOrDefault(key) is Dictionary<string, object?> nested &&
            nested.GetValueOrDefault(inner) is string s && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (args.Arguments.TryGetValue("action", out var action) && action == "openTopic" &&
                args.Arguments.TryGetValue("topicId", out var tidStr) &&
                int.TryParse(tidStr, out var tid))
            {
                int? post = args.Arguments.TryGetValue("postNumber", out var pnStr) && int.TryParse(pnStr, out var pn)
                    ? pn : null;
                App.DispatcherQueue?.TryEnqueue(() =>
                    AppRouter.Current.OpenTopic(tid, null, post));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("toast", "invoke: " + ex.Message);
        }
    }
}
