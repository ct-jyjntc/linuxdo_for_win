using LinuxDo.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LinuxDo.Controls;

/// <summary>Image that loads via cookie-aware ImageLoader (memory + disk cache).</summary>
public sealed class CachedImage : Control
{
    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.Register(
            nameof(SourceUrl),
            typeof(string),
            typeof(CachedImage),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(CachedImage),
            new PropertyMetadata(Stretch.Uniform, OnStretchChanged));

    private Image? _image;
    private ProgressRing? _ring;
    private int _loadGen;
    private string? _appliedUrl;

    public CachedImage()
    {
        DefaultStyleKey = typeof(CachedImage);
        // Re-load when becoming visible (footer avatar starts Collapsed)
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible)
                _ = LoadAsync(force: false);
        });
    }

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _image = GetTemplateChild("PART_Image") as Image;
        _ring = GetTemplateChild("PART_Ring") as ProgressRing;
        if (_image is not null)
            _image.Stretch = Stretch;
        _appliedUrl = null;
        _ = LoadAsync(force: true);
    }

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CachedImage ci)
        {
            ci._appliedUrl = null;
            _ = ci.LoadAsync(force: true);
        }
    }

    private static void OnStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CachedImage { _image: not null } ci)
            ci._image.Stretch = ci.Stretch;
    }

    private async Task LoadAsync(bool force)
    {
        var gen = ++_loadGen;
        var url = SourceUrl;

        // Template may not be ready yet
        if (_image is null) return;
        if (Visibility != Visibility.Visible && string.IsNullOrWhiteSpace(url))
        {
            _image.Source = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            _image.Source = null;
            _appliedUrl = null;
            if (_ring is not null) _ring.IsActive = false;
            return;
        }

        if (!force && string.Equals(_appliedUrl, url, StringComparison.Ordinal) && _image.Source is not null)
            return;

        // Instant memory hit (already UI-thread BitmapImage)
        var cached = ImageLoader.Shared.Cached(url);
        if (cached is not null)
        {
            _image.Source = cached;
            _appliedUrl = url;
            if (_ring is not null) _ring.IsActive = false;
            return;
        }

        if (_ring is not null) _ring.IsActive = true;

        BitmapImage? bmp = null;
        try
        {
            bmp = await ImageLoader.Shared.LoadAsync(url);
        }
        catch
        {
            bmp = null;
        }

        if (gen != _loadGen) return;

        // Ensure we set Image.Source on UI thread
        void Apply()
        {
            if (gen != _loadGen || _image is null) return;
            if (_ring is not null) _ring.IsActive = false;

            if (bmp is not null)
            {
                _image.Source = bmp;
                _appliedUrl = url;
                return;
            }

            // Fallback: plain BitmapImage (public CDN / non-CF)
            try
            {
                var fallback = new BitmapImage();
                fallback.UriSource = new Uri(url);
                _image.Source = fallback;
                _appliedUrl = url;
            }
            catch
            {
                _image.Source = null;
                _appliedUrl = null;
            }
        }

        var dq = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq is not null && !dq.HasThreadAccess)
            dq.TryEnqueue(Apply);
        else
            Apply();
    }
}
