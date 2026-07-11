using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace LinuxDo.Features.Topic;

public sealed partial class ImageViewerDialog : ContentDialog
{
    private readonly string _url;
    private bool _fitted = true;
    private float _fitFactor = 1f;

    public ImageViewerDialog(string url)
    {
        InitializeComponent();
        _url = url;
        UrlText.Text = url + "  ·  双击切换适应/100%  ·  Shift+滚轮横向平移";
        Loaded += async (_, _) =>
        {
            try
            {
                var bmp = await ImageLoader.Shared.LoadAsync(url);
                PreviewImage.Source = bmp ?? new BitmapImage(new Uri(url));
                await Task.Delay(100);
                Fit_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                UrlText.Text = "加载失败：" + ex.Message;
            }
        };

        PreviewImage.DoubleTapped += PreviewImage_DoubleTapped;
        Scroller.PointerWheelChanged += Scroller_PointerWheelChanged;
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ZoomText.Text = $"{Scroller.ZoomFactor * 100:0}%";
    }

    private void PreviewImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_fitted)
            Actual_Click(sender, new RoutedEventArgs());
        else
            Fit_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Shift + wheel → horizontal pan (mac parity)
        var point = e.GetCurrentPoint(Scroller);
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!shift) return;

        var delta = point.Properties.MouseWheelDelta;
        var newX = Scroller.HorizontalOffset - delta;
        Scroller.ChangeView(newX, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PreviewImage.ActualWidth <= 0 || Scroller.ActualWidth <= 0)
            {
                Scroller.ChangeView(null, null, 1f);
                _fitted = true;
                return;
            }
            var sx = (float)(Scroller.ActualWidth / Math.Max(PreviewImage.ActualWidth, 1));
            var sy = (float)(Scroller.ActualHeight / Math.Max(PreviewImage.ActualHeight, 1));
            _fitFactor = Math.Clamp(Math.Min(sx, sy), 0.1f, 8f);
            Scroller.ChangeView(0, 0, _fitFactor);
            _fitted = true;
        }
        catch
        {
            Scroller.ChangeView(null, null, 1f);
            _fitted = true;
        }
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        Scroller.ChangeView(null, null, 1f);
        _fitted = false;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        Scroller.ChangeView(null, null, Math.Min(8f, Scroller.ZoomFactor * 1.25f));
        _fitted = false;
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        Scroller.ChangeView(null, null, Math.Max(0.1f, Scroller.ZoomFactor / 1.25f));
        _fitted = false;
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            byte[] data;
            try
            {
                using var http = CookieSessionBridge.CreateHttpClient();
                http.DefaultRequestHeaders.Remove("Accept");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");
                data = await http.GetByteArrayAsync(_url);
            }
            catch
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CookieSessionBridge.UserAgent);
                data = await http.GetByteArrayAsync(_url);
            }
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.SuggestedFileName = "linuxdo-image";
            picker.FileTypeChoices.Add("PNG", [".png"]);
            picker.FileTypeChoices.Add("JPEG", [".jpg"]);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
                await FileIO.WriteBytesAsync(file, data);
            else
                args.Cancel = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            UrlText.Text = "保存失败：" + ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CopyUrl_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var dp = new DataPackage();
        dp.SetText(_url);
        Clipboard.SetContent(dp);
        args.Cancel = true;
        UrlText.Text = "地址已复制 · " + _url;
    }
}
