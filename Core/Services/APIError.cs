using System.Text;

namespace LinuxDo.Core.Services;

public abstract class APIError : Exception
{
    protected APIError(string message) : base(message) { }

    public virtual bool IsChallengeRelated => false;

    public sealed class InvalidUrl : APIError
    {
        public InvalidUrl() : base("无效的请求地址") { }
    }

    public sealed class InvalidResponse : APIError
    {
        public InvalidResponse() : base("服务器响应无效") { }
    }

    public sealed class Http : APIError
    {
        public int Status { get; }
        public string? BodyMessage { get; }

        public Http(int status, string? message)
            : base(FormatMessage(status, message))
        {
            Status = status;
            BodyMessage = message;
        }

        private static string FormatMessage(int status, string? message)
        {
            // status 0 is not a real HTTP code — browser/WebView transport failure.
            if (status == 0)
            {
                return string.IsNullOrEmpty(message)
                    ? "网络请求中断，请稍后重试"
                    : $"网络请求中断：{message}";
            }
            return string.IsNullOrEmpty(message)
                ? $"请求失败（HTTP {status}）"
                : $"请求失败（{status}）：{message}";
        }

        public override bool IsChallengeRelated
        {
            get
            {
                if (Status is not (403 or 503)) return false;
                var text = (BodyMessage ?? "").ToLowerInvariant();
                return text.Contains("cloudflare") || text.Contains("just a moment")
                       || text.Contains("cf-") || text.Contains("challenge");
            }
        }
    }

    public sealed class Unauthorized : APIError
    {
        public Unauthorized() : base("未授权，请重新登录") { }
    }

    public sealed class Forbidden : APIError
    {
        public Forbidden() : base("没有权限执行此操作（可能未登录、CSRF 失效，或需要重新完成站点验证）") { }
    }

    public sealed class RateLimited : APIError
    {
        public RateLimited() : base("请求过于频繁，请稍后再试") { }
    }

    public sealed class Decoding : APIError
    {
        public Decoding(Exception inner) : base($"数据解析失败：{inner.Message}") { }
    }

    public sealed class Network : APIError
    {
        public Network(Exception inner)
            : base(string.IsNullOrWhiteSpace(inner.Message)
                ? "网络请求失败，请检查网络后重试"
                : NormalizeNetworkMessage(inner.Message))
        {
        }

        private static string NormalizeNetworkMessage(string message)
        {
            if (message.Contains("HTTP 0", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("status 0", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("status=0", StringComparison.OrdinalIgnoreCase))
                return "网络请求中断，请稍后重试";
            if (message.Contains("WebView fetch timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out after", StringComparison.OrdinalIgnoreCase))
                return "请求超时（页面通道繁忙或站点较慢），请稍后再试";
            if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("aborted", StringComparison.OrdinalIgnoreCase))
                return "请求超时或被中断，请稍后再试";
            return message;
        }
    }

    public sealed class MissingAuth : APIError
    {
        public MissingAuth() : base("请先登录") { }
    }

    public sealed class ServerMessage : APIError
    {
        public ServerMessage(string message) : base(message) { }
    }

    public sealed class CloudflareChallenge : APIError
    {
        public CloudflareChallenge() : base("站点开启了人机验证（Cloudflare）。请完成验证后重试。") { }
        public override bool IsChallengeRelated => true;
    }

    public sealed class NonJsonResponse : APIError
    {
        public NonJsonResponse() : base("服务器返回了非 JSON 内容（可能是防护页）") { }
    }

    public static void PostIfChallenge(Exception error)
    {
        if (error is APIError api)
        {
            if (api is CloudflareChallenge || api.IsChallengeRelated)
            {
                AppEvents.RaiseApiError(error);
                return;
            }
        }
        var text = error.Message.ToLowerInvariant();
        if (text.Contains("cloudflare") || text.Contains("人机验证") || text.Contains("just a moment"))
            AppEvents.RaiseApiError(error);
    }
}

public static class ResponseInspector
{
    public static bool LooksLikeCloudflare(byte[] data, int status)
    {
        if (IsProbablyJson(data)) return false;
        var prefix = Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 4000))).ToLowerInvariant();

        string[] htmlMarkers =
        [
            "just a moment", "cf-browser-verification", "challenge-platform",
            "enable javascript and cookies", "cf-challenge", "checking your browser",
            "verify you are human", "attention required",
            "needs to review the security of your connection",
            "确认您是真人", "正在验证您是否是真人", "cf-chl-bypass",
            "cf-turnstile", "cdn-cgi/challenge-platform"
        ];
        if (htmlMarkers.Any(m => prefix.Contains(m))) return true;

        if ((status is 403 or 503) && (prefix.Contains("<html") || prefix.Contains("<!doctype")))
            return true;

        if (status == 200 && LooksLikeHtml(data) &&
            prefix.Contains("cloudflare") &&
            (prefix.Contains("challenge") || prefix.Contains("just a moment")))
            return true;

        return false;
    }

    public static bool LooksLikeHtml(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 200)))
            .TrimStart().ToLowerInvariant();
        return text.StartsWith("<!doctype") || text.StartsWith("<html") || text.StartsWith("<head");
    }

    public static bool IsProbablyJson(byte[] data)
    {
        foreach (var b in data)
        {
            if (b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') continue;
            return b is (byte)'{' or (byte)'[';
        }
        return false;
    }
}

/// <summary>App-wide lightweight event bus (replaces NotificationCenter).</summary>
public static class AppEvents
{
    public static event Action? Refresh;
    public static event Action<Exception>? ApiError;
    public static event Action? NavigateNext;
    public static event Action? NavigatePrev;
    public static event Action? QuickAction;
    public static event Action<string, Dictionary<string, object?>>? TopicBus;

    public static void RaiseRefresh() => Refresh?.Invoke();
    public static void RaiseApiError(Exception ex) => ApiError?.Invoke(ex);
    public static void RaiseNavigateNext() => NavigateNext?.Invoke();
    public static void RaiseNavigatePrev() => NavigatePrev?.Invoke();
    public static void RaiseQuickAction() => QuickAction?.Invoke();
    public static void RaiseTopicBus(string channel, Dictionary<string, object?> data)
        => TopicBus?.Invoke(channel, data);
}

/// <summary>Global keyboard shortcut ids (Ctrl/Cmd equivalents on Windows).</summary>
public enum AppShortcutAction
{
    NewTopic,
    NewMessage,
    Search,
    Refresh,
    GoLatest,
    GoTop,
    GoUnread,
    GoNotifications,
    GoMessages,
    GoReadLater,
    GoHistory,
    ListNext,
    ListPrev,
    ListOpen
}

public static class AppShortcutActionExtensions
{
    public static string Title(this AppShortcutAction a) => a switch
    {
        AppShortcutAction.NewTopic => "新建主题",
        AppShortcutAction.NewMessage => "写私信",
        AppShortcutAction.Search => "搜索",
        AppShortcutAction.Refresh => "刷新",
        AppShortcutAction.GoLatest => "最新",
        AppShortcutAction.GoTop => "热门",
        AppShortcutAction.GoUnread => "未读",
        AppShortcutAction.GoNotifications => "通知",
        AppShortcutAction.GoMessages => "私信",
        AppShortcutAction.GoReadLater => "稍后阅读",
        AppShortcutAction.GoHistory => "浏览历史",
        AppShortcutAction.ListNext => "列表下一项",
        AppShortcutAction.ListPrev => "列表上一项",
        AppShortcutAction.ListOpen => "打开当前选中",
        _ => a.ToString()
    };

    public static string DefaultDisplay(this AppShortcutAction a) => a switch
    {
        AppShortcutAction.NewTopic => "Ctrl+N",
        AppShortcutAction.NewMessage => "Ctrl+Shift+N",
        AppShortcutAction.Search => "Ctrl+F",
        AppShortcutAction.Refresh => "Ctrl+R",
        AppShortcutAction.GoLatest => "Ctrl+1",
        AppShortcutAction.GoTop => "Ctrl+2",
        AppShortcutAction.GoUnread => "Ctrl+3",
        AppShortcutAction.GoNotifications => "Ctrl+4",
        AppShortcutAction.GoMessages => "Ctrl+5",
        AppShortcutAction.GoReadLater => "Ctrl+6",
        AppShortcutAction.GoHistory => "Ctrl+7",
        AppShortcutAction.ListNext => "Ctrl+J",
        AppShortcutAction.ListPrev => "Ctrl+K",
        AppShortcutAction.ListOpen => "Ctrl+Enter",
        _ => ""
    };
}
