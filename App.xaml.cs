using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace LinuxDo;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Error("app", e.Exception?.Message ?? e.Message);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        SystemToast.EnsureRegistered();

        // Protocol activation (linuxdo://...)
        try
        {
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == ExtendedActivationKind.Protocol &&
                activated.Data is IProtocolActivatedEventArgs protocol &&
                protocol.Uri is not null)
            {
                HandleProtocol(protocol.Uri);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("app", "protocol: " + ex.Message);
        }

        Window.Activate();
    }

    public static void HandleProtocol(Uri uri)
    {
        // User API Key auth callback: linuxdo://auth?payload=…
        if (IsAuthCallback(uri))
        {
            DispatcherQueue?.TryEnqueue(async () =>
            {
                await UserSessionStore.Current.CompleteUserApiAuthAsync(uri);
            });
            return;
        }

        var route = DeepLinkRouter.RouteFrom(uri);
        if (route is null) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (route.IsRoot) AppRouter.Current.SelectRoot(route);
            else AppRouter.Current.Push(route);
        });
    }

    private static bool IsAuthCallback(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "linuxdo", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase))
            return true;
        if (uri.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return true;
        return uri.Query.Contains("payload=", StringComparison.OrdinalIgnoreCase)
               || (uri.Fragment?.Contains("payload=", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
