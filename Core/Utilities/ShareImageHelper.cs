using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace LinuxDo.Core.Utilities;

/// <summary>Renders a share card via offscreen XAML + RenderTargetBitmap.</summary>
public static class ShareImageHelper
{
    public static async Task<byte[]?> RenderPngAsync(
        XamlRoot xamlRoot,
        string title,
        string? author,
        string body,
        string url,
        string siteHost = "linux.do",
        bool dark = false)
    {
        try
        {
            var bg = dark ? ColorHelper.FromArgb(255, 28, 30, 36) : Colors.White;
            var card = dark ? ColorHelper.FromArgb(255, 40, 44, 52) : ColorHelper.FromArgb(255, 247, 247, 250);
            var primary = dark ? Colors.White : ColorHelper.FromArgb(255, 20, 20, 20);
            var secondary = dark ? ColorHelper.FromArgb(180, 255, 255, 255) : ColorHelper.FromArgb(140, 0, 0, 0);
            var accent = ColorHelper.FromArgb(255, 0, 120, 212);

            var content = body.Length > 600 ? body[..600] + "…" : body;
            var authorText = string.IsNullOrEmpty(author) ? ""
                : author!.StartsWith('@') ? author : "@" + author;

            var root = new Border
            {
                Width = 420,
                Background = new SolidColorBrush(bg),
                Child = new StackPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Background = new SolidColorBrush(card),
                            Padding = new Thickness(20, 14, 20, 14),
                            Child = new Grid
                            {
                                ColumnDefinitions =
                                {
                                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                                    new ColumnDefinition { Width = GridLength.Auto }
                                },
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = siteHost,
                                        FontSize = 12,
                                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                        Foreground = new SolidColorBrush(secondary)
                                    },
                                    Named(new TextBlock
                                    {
                                        Text = "LinuxDo",
                                        FontSize = 11,
                                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                                        Foreground = new SolidColorBrush(accent),
                                        HorizontalAlignment = HorizontalAlignment.Right
                                    }, 1)
                                }
                            }
                        },
                        new StackPanel
                        {
                            Padding = new Thickness(20, 16, 20, 20),
                            Spacing = 10,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = title,
                                    FontSize = 18,
                                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                                    Foreground = new SolidColorBrush(primary),
                                    TextWrapping = TextWrapping.WrapWholeWords
                                },
                                new TextBlock
                                {
                                    Text = authorText,
                                    FontSize = 12,
                                    Foreground = new SolidColorBrush(secondary),
                                    Visibility = string.IsNullOrEmpty(authorText) ? Visibility.Collapsed : Visibility.Visible
                                },
                                new TextBlock
                                {
                                    Text = content,
                                    FontSize = 13,
                                    Foreground = new SolidColorBrush(primary),
                                    TextWrapping = TextWrapping.WrapWholeWords,
                                    MaxLines = 18,
                                    TextTrimming = TextTrimming.CharacterEllipsis
                                },
                                new TextBlock
                                {
                                    Text = url,
                                    FontSize = 11,
                                    FontFamily = new FontFamily("Consolas"),
                                    Foreground = new SolidColorBrush(secondary),
                                    TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 8, 0, 0)
                                }
                            }
                        }
                    }
                }
            };

            // Host off-tree in a Popup-like measure pass
            var host = new Canvas { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
            // Attach via temporary visual tree under XamlRoot content if possible
            if (xamlRoot.Content is FrameworkElement pageRoot)
            {
                // Use a hidden container on the root grid if available
                if (pageRoot is Panel panel)
                {
                    panel.Children.Add(host);
                    host.Children.Add(root);
                }
                else
                {
                    // Fallback: just measure without attaching (may still work for RTB in some versions)
                    host.Children.Add(root);
                }
            }
            else
            {
                host.Children.Add(root);
            }

            root.Measure(new Windows.Foundation.Size(420, 4000));
            root.Arrange(new Windows.Foundation.Rect(0, 0, 420, root.DesiredSize.Height));
            root.UpdateLayout();

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var pixels = await rtb.GetPixelsAsync();

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96, 96,
                pixels.ToArray());
            await encoder.FlushAsync();

            // cleanup host
            if (host.Parent is Panel p) p.Children.Remove(host);

            stream.Seek(0);
            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            AppLog.Warning("share", ex.Message);
            return null;
        }
    }

    private static FrameworkElement Named(FrameworkElement el, int col)
    {
        Grid.SetColumn(el, col);
        return el;
    }

    public static async Task SavePngAsync(byte[] data, string suggestedName = "linuxdo-share.png")
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add("PNG", [".png"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await FileIO.WriteBytesAsync(file, data);
    }

    public static async Task CopyPngToClipboardAsync(byte[] data)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "linuxdo-share.png");
            await File.WriteAllBytesAsync(temp, data);
            var file = await StorageFile.GetFileFromPathAsync(temp);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetStorageItems(new[] { file });
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch (Exception ex)
        {
            AppLog.Warning("share", "clipboard: " + ex.Message);
        }
    }
}
