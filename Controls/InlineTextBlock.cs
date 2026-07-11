using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace LinuxDo.Controls;

/// <summary>Renders InlineSpan list as a selectable RichTextBlock (bold / italic / code / links / @mentions).</summary>
public sealed class InlineTextBlock : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InlineTextBlock),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.Register(nameof(Html), typeof(string), typeof(InlineTextBlock),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty SpansProperty =
        DependencyProperty.Register(nameof(Spans), typeof(object), typeof(InlineTextBlock),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty ContentFontSizeProperty =
        DependencyProperty.Register(nameof(ContentFontSize), typeof(double), typeof(InlineTextBlock),
            new PropertyMetadata(15.0, OnContentChanged));

    public static readonly DependencyProperty ContentLineHeightProperty =
        DependencyProperty.Register(nameof(ContentLineHeight), typeof(double), typeof(InlineTextBlock),
            new PropertyMetadata(0.0, OnContentChanged));

    private RichTextBlock? _rtb;

    public InlineTextBlock()
    {
        DefaultStyleKey = typeof(InlineTextBlock);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Html
    {
        get => (string?)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public object? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public double ContentFontSize
    {
        get => (double)GetValue(ContentFontSizeProperty);
        set => SetValue(ContentFontSizeProperty, value);
    }

    public double ContentLineHeight
    {
        get => (double)GetValue(ContentLineHeightProperty);
        set => SetValue(ContentLineHeightProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rtb = GetTemplateChild("PART_RichText") as RichTextBlock;
        Rebuild();
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InlineTextBlock b) b.Rebuild();
    }

    private void Rebuild()
    {
        if (_rtb is null) return;
        _rtb.Blocks.Clear();
        var size = ContentFontSize > 0 ? ContentFontSize : 15;
        _rtb.FontSize = size;
        if (ContentLineHeight > 0) _rtb.LineHeight = ContentLineHeight;

        IReadOnlyList<InlineSpan> spans;
        if (Spans is IReadOnlyList<InlineSpan> list && list.Count > 0)
            spans = list;
        else if (!string.IsNullOrEmpty(Html))
            spans = InlineText.FromHtml(Html);
        else if (!string.IsNullOrEmpty(Text))
            spans = InlineText.FromPlain(Text);
        else
            spans = [];

        Brush? mentionBrush = null;
        try
        {
            mentionBrush = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush;
        }
        catch { /* ignore */ }

        var para = new Paragraph();
        foreach (var s in spans)
        {
            if (string.IsNullOrEmpty(s.Text)) continue;

            if (s.IsMention && !string.IsNullOrEmpty(s.MentionUser))
            {
                var user = s.MentionUser!;
                var link = new Hyperlink();
                link.Inlines.Add(MakeRun(s, size, mentionBrush));
                link.Click += (_, args) =>
                {
                    AppRouter.Current.OpenUser(user);
                };
                para.Inlines.Add(link);
            }
            else if (!string.IsNullOrEmpty(s.LinkUrl))
            {
                Uri? nav = null;
                if (Uri.TryCreate(s.LinkUrl, UriKind.Absolute, out var abs))
                    nav = abs;
                else
                {
                    try { nav = new Uri(AppSettings.Current.BaseUrl, s.LinkUrl); }
                    catch { /* ignore */ }
                }

                // In-app topic/user routes
                if (nav is not null && IsForumLink(nav, out var route))
                {
                    var link = new Hyperlink();
                    link.Inlines.Add(MakeRun(s, size, mentionBrush));
                    link.Click += (_, _) =>
                    {
                        if (route!.IsRoot) AppRouter.Current.SelectRoot(route);
                        else AppRouter.Current.Push(route);
                    };
                    para.Inlines.Add(link);
                }
                else if (nav is not null)
                {
                    var link = new Hyperlink { NavigateUri = nav };
                    link.Inlines.Add(MakeRun(s, size, mentionBrush));
                    para.Inlines.Add(link);
                }
                else
                {
                    para.Inlines.Add(MakeRun(s, size, mentionBrush));
                }
            }
            else
            {
                para.Inlines.Add(MakeRun(s, size, mentionBrush));
            }
        }
        _rtb.Blocks.Add(para);
    }

    private static Run MakeRun(InlineSpan s, double baseSize, Brush? mentionBrush)
    {
        var run = new Run { Text = s.Text };
        if (s.Bold || s.IsMention) run.FontWeight = FontWeights.SemiBold;
        if (s.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (s.Code)
        {
            run.FontFamily = new FontFamily("Consolas");
            run.FontSize = Math.Max(11, baseSize - 1);
        }
        if (s.IsMention && mentionBrush is not null)
            run.Foreground = mentionBrush;
        return run;
    }

    private static bool IsForumLink(Uri uri, out AppRoute? route)
    {
        route = DeepLinkRouter.RouteFrom(uri);
        return route is not null;
    }
}
