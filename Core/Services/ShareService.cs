using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using LinuxDo.Core.Utilities;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace LinuxDo.Core.Services;

/// <summary>Windows Share UI (DataTransferManager) helpers for WinUI desktop.</summary>
public static class ShareService
{
    private static string? _pendingTitle;
    private static string? _pendingText;
    private static string? _pendingUrl;
    private static byte[]? _pendingPng;

    // Keep one manager per HWND
    private static readonly Dictionary<nint, DataTransferManager> Managers = new();

    public static void ShareLink(nint hwnd, string title, string url, string? text = null)
    {
        _pendingTitle = title;
        _pendingUrl = url;
        _pendingText = text ?? url;
        _pendingPng = null;
        ShowShareUI(hwnd);
    }

    public static void ShareText(nint hwnd, string title, string text)
    {
        _pendingTitle = title;
        _pendingText = text;
        _pendingUrl = null;
        _pendingPng = null;
        ShowShareUI(hwnd);
    }

    public static void SharePng(nint hwnd, string title, byte[] png, string? text = null)
    {
        _pendingTitle = title;
        _pendingPng = png;
        _pendingText = text;
        _pendingUrl = null;
        ShowShareUI(hwnd);
    }

    private static void ShowShareUI(nint hwnd)
    {
        try
        {
            EnsureManager(hwnd);
            // WinAppSDK / desktop: IDataTransferManagerInterop.ShowShareUIForWindow
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warning("share", ex.Message);
            // Fallback: copy to clipboard
            try
            {
                var dp = new DataPackage();
                if (!string.IsNullOrEmpty(_pendingUrl))
                    dp.SetText(_pendingUrl);
                else if (!string.IsNullOrEmpty(_pendingText))
                    dp.SetText(_pendingText);
                Clipboard.SetContent(dp);
            }
            catch { /* ignore */ }
        }
    }

    private static void EnsureManager(nint hwnd)
    {
        if (Managers.ContainsKey(hwnd)) return;

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = typeof(IDataTransferManager).GUID;
        // IDataTransferManager GUID
        var riid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
        var ptr = interop.GetForWindow(hwnd, ref riid);
        var dtm = MarshalInterface<DataTransferManager>.FromAbi(ptr);
        dtm.DataRequested += OnDataRequested;
        Managers[hwnd] = dtm;
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var request = args.Request;
        request.Data.Properties.Title = _pendingTitle ?? "LinuxDo";
        if (!string.IsNullOrEmpty(_pendingText))
            request.Data.SetText(_pendingText);
        if (!string.IsNullOrEmpty(_pendingUrl) && Uri.TryCreate(_pendingUrl, UriKind.Absolute, out var uri))
            request.Data.SetWebLink(uri);

        if (_pendingPng is { Length: > 0 })
        {
            var deferral = request.GetDeferral();
            try
            {
                var folder = ApplicationData.Current.TemporaryFolder;
                var file = await folder.CreateFileAsync(
                    $"linuxdo-share-{DateTimeOffset.Now.ToUnixTimeSeconds()}.png",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, _pendingPng);
                request.Data.SetStorageItems(new[] { file });
                request.Data.Properties.Description = "分享图片";
            }
            catch (Exception ex)
            {
                request.FailWithDisplayText("无法分享图片：" + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    // Marker for IID lookup (not used at runtime)
    private interface IDataTransferManager { }
}
