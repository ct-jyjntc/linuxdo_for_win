using LinuxDo.Controls;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace LinuxDo.Features.Compose;

public sealed partial class ComposeDialog : ContentDialog
{
    private readonly ComposeContext _context;
    private Guid? _draftId;
    private string? _serverKey;
    private int _serverSequence;
    private CancellationTokenSource? _mentionCts;
    private CancellationTokenSource? _autosaveCts;
    private static readonly string[] Emojis =
        ["👍", "😄", "🎉", "❤️", "🔥", "👀", "🙏", "😂", "🤔", "✅", "❌", "🚀"];

    public ComposeDialog(ComposeContext context)
    {
        InitializeComponent();
        _context = context;
        _draftId = context.DraftId;
        Title = context.IsEditing ? "编辑帖子"
            : context.IsPrivateMessage ? "写私信"
            : context.IsReply ? "回复"
            : "新建主题";

        TitleBox.Text = context.Title;
        BodyBox.Text = context.Body;
        if (context.Tags.Count > 0) TagsBox.Text = string.Join(", ", context.Tags);
        if (context.IsPrivateMessage)
        {
            RecipientsBox.Visibility = Visibility.Visible;
            RecipientsBox.Text = context.PmRecipients;
            CategoryBox.Visibility = Visibility.Collapsed;
            TagsBox.Visibility = Visibility.Collapsed;
        }
        if (context.IsReply || context.IsEditing)
        {
            TitleBox.Visibility = Visibility.Collapsed;
            CategoryBox.Visibility = Visibility.Collapsed;
            TagsBox.Visibility = Visibility.Collapsed;
        }

        // Restore server key from matching local draft
        if (context.DraftId is Guid did)
        {
            var existing = DraftStore.Current.Drafts.FirstOrDefault(d => d.Id == did);
            if (existing is not null)
            {
                _serverKey = existing.ServerKey;
                _serverSequence = existing.ServerSequence ?? 0;
            }
        }
        _serverKey ??= DraftStore.DraftKeyFor(context);

        Loaded += async (_, _) =>
        {
            if (CategoryBox.Visibility == Visibility.Visible)
            {
                if (CategoryStore.Current.Categories.Count == 0)
                    await CategoryStore.Current.LoadAsync();
                CategoryBox.ItemsSource = CategoryStore.Current.TopLevelCategories
                    .Select(c => new CategoryOption(c.Id, c.Name)).ToList();
                if (context.CategoryId is int cid)
                {
                    var items = CategoryBox.ItemsSource as IEnumerable<CategoryOption>;
                    CategoryBox.SelectedItem = items?.FirstOrDefault(c => c.Id == cid);
                }
            }
        };
    }

    private sealed record CategoryOption(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    private async void Publish_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        ErrorText.Text = "";
        StatusText.Text = "发布中…";
        try
        {
            // Force cookie + fresh CSRF every publish attempt (cookie sessions go stale after CF).
            await DiscourseAPI.Shared.PrepareWriteSessionAsync();
            var body = BodyBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("正文不能为空");

            if (_context.IsEditing && _context.EditPostId is int editId)
            {
                await DiscourseAPI.Shared.EditPostAsync(editId, body);
            }
            else if (_context.IsPrivateMessage)
            {
                var title = TitleBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("私信需要标题");
                var recipients = (RecipientsBox.Text ?? "")
                    .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (recipients.Count == 0) throw new InvalidOperationException("请填写收件人");
                await DiscourseAPI.Shared.CreatePrivateMessageAsync(title, body, recipients);
            }
            else if (_context.IsReply && _context.TopicId is int topicId)
            {
                await DiscourseAPI.Shared.ReplyAsync(topicId, body, _context.ReplyToPostNumber);
            }
            else
            {
                var title = TitleBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("主题需要标题");
                int? categoryId = (CategoryBox.SelectedItem as CategoryOption)?.Id ?? _context.CategoryId;
                var tags = (TagsBox.Text ?? "")
                    .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                var resp = await DiscourseAPI.Shared.CreateTopicAsync(title, body, categoryId, tags);
                if (resp.TopicId is int newId)
                    AppRouter.Current.OpenTopic(newId, title);
            }

            // Clear local + server draft after successful publish
            if (_draftId is Guid did)
            {
                var existing = DraftStore.Current.Drafts.FirstOrDefault(d => d.Id == did);
                if (existing is not null)
                    await DraftStore.Current.DeleteFromServerAsync(existing);
                else
                    DraftStore.Current.Delete(did);
            }
            else if (!string.IsNullOrEmpty(_serverKey) && !_context.IsEditing)
            {
                try
                {
                    await DiscourseAPI.Shared.PrepareWriteSessionAsync();
                    await DiscourseAPI.Shared.DeleteDraftAsync(_serverKey!, _serverSequence);
                }
                catch { /* best effort */ }
            }
            StatusText.Text = "已发布";
            AppEvents.RaiseRefresh();
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ErrorText.Text = ex.Message;
            StatusText.Text = "";
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void SaveDraft_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // keep dialog open
        StatusText.Text = "保存草稿中…";
        try
        {
            var draft = BuildDraft();
            DraftStore.Current.Save(draft);
            _draftId = draft.Id;
            if (AppSettings.Current.AutosaveServerDrafts && UserSessionStore.Current.IsLoggedIn && !_context.IsEditing)
            {
                var key = _serverKey ?? DraftStore.DraftKeyFor(draft);
                draft = await DraftStore.Current.SaveToServerAsync(draft, key);
                _serverKey = draft.ServerKey;
                _serverSequence = draft.ServerSequence ?? 0;
                StatusText.Text = "草稿已同步到服务器";
            }
            else
            {
                StatusText.Text = "草稿已本地保存";
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            StatusText.Text = "本地草稿已保存；云同步失败";
            APIError.PostIfChallenge(ex);
        }
    }

    private ComposeDraft BuildDraft()
    {
        var draft = new ComposeDraft
        {
            Id = _draftId ?? Guid.NewGuid(),
            Title = TitleBox.Text ?? "",
            Body = BodyBox.Text ?? "",
            CategoryId = (CategoryBox.SelectedItem as CategoryOption)?.Id ?? _context.CategoryId,
            TopicId = _context.TopicId,
            ReplyToPostNumber = _context.ReplyToPostNumber,
            IsPrivateMessage = _context.IsPrivateMessage,
            PmRecipients = RecipientsBox.Text,
            Tags = (TagsBox.Text ?? "")
                .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ServerKey = _serverKey,
            ServerSequence = _serverSequence
        };
        _draftId = draft.Id;
        return draft;
    }

    private void SaveLocalDraft()
    {
        var draft = BuildDraft();
        DraftStore.Current.Save(draft);
    }

    private async Task AutosaveAsync()
    {
        var draft = BuildDraft();
        DraftStore.Current.Save(draft);
        if (!AppSettings.Current.AutosaveServerDrafts) return;
        if (!UserSessionStore.Current.IsLoggedIn || _context.IsEditing) return;
        if (string.IsNullOrWhiteSpace(draft.Body) && string.IsNullOrWhiteSpace(draft.Title)) return;
        try
        {
            var key = _serverKey ?? DraftStore.DraftKeyFor(draft);
            draft = await DraftStore.Current.SaveToServerAsync(draft, key);
            _serverKey = draft.ServerKey;
            _serverSequence = draft.ServerSequence ?? 0;
            StatusText.Text = "草稿已自动云同步";
        }
        catch (Exception ex)
        {
            StatusText.Text = "草稿已本地保存";
            AppLog.Warning("drafts", "autosave server: " + ex.Message);
        }
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => WrapSelection("**", "**");
    private void Italic_Click(object sender, RoutedEventArgs e) => WrapSelection("*", "*");
    private void Code_Click(object sender, RoutedEventArgs e) => WrapSelection("`", "`");
    private void Quote_Click(object sender, RoutedEventArgs e) => WrapSelection("> ", "");
    private void Link_Click(object sender, RoutedEventArgs e) => WrapSelection("[", "](url)");
    private void List_Click(object sender, RoutedEventArgs e) => WrapSelection("- ", "");

    private async void Emoji_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView
        {
            ItemsSource = Emojis,
            SelectionMode = ListViewSelectionMode.Single,
            Height = 200,
            Width = 240
        };
        var dialog = new ContentDialog
        {
            Title = "插入 Emoji",
            Content = list,
            PrimaryButtonText = "插入",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string emoji)
            InsertAtCursor(emoji);
    }

    private async void Template_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "加载模板…";
            var templates = await DiscourseAPI.Shared.FetchTemplatesAsync();
            if (templates.Count == 0)
            {
                StatusText.Text = "站点未提供发帖模板";
                return;
            }
            var list = new ListView
            {
                ItemsSource = templates.Select(t => t.DisplayTitle).ToList(),
                SelectionMode = ListViewSelectionMode.Single,
                Height = 280,
                Width = 360,
                SelectedIndex = 0
            };
            var dialog = new ContentDialog
            {
                Title = "插入发帖模板",
                Content = list,
                PrimaryButtonText = "插入",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0)
            {
                var body = templates[list.SelectedIndex].Body;
                if (!string.IsNullOrEmpty(body))
                    InsertAtCursor(body);
                StatusText.Text = "模板已插入";
            }
            else StatusText.Text = "";
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            StatusText.Text = "";
            APIError.PostIfChallenge(ex);
        }
    }

    private CancellationTokenSource? _previewCts;

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var show = PreviewPane.Visibility != Visibility.Visible;
        PreviewPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (PreviewPane.Visibility != Visibility.Visible) return;
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var md = BodyBox.Text ?? "";
        try
        {
            await Task.Delay(200, token);
        }
        catch (TaskCanceledException) { return; }

        var baseUrl = AppSettings.Current.BaseUrl;
        var blocks = await Task.Run(() => ComposeMarkdownParser.Parse(md, baseUrl), token);
        if (token.IsCancellationRequested) return;

        PreviewHost.Children.Clear();
        if (blocks.Count == 0)
        {
            PreviewHost.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(md) ? "预览为空" : "…",
                Opacity = 0.6
            });
            return;
        }

        foreach (var block in blocks)
        {
            if (token.IsCancellationRequested) return;
            switch (block)
            {
                case ComposeMarkdownParser.Block.Heading h:
                    PreviewHost.Children.Add(new TextBlock
                    {
                        Text = h.Text,
                        FontSize = 18 - Math.Min(h.Level, 3),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
                case ComposeMarkdownParser.Block.Paragraph p:
                    PreviewHost.Children.Add(new TextBlock
                    {
                        Text = p.Text,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                    break;
                case ComposeMarkdownParser.Block.Quote q:
                    PreviewHost.Children.Add(new Border
                    {
                        Padding = new Thickness(10, 6, 6, 6),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                        CornerRadius = new CornerRadius(4),
                        Child = new TextBlock
                        {
                            Text = q.Text,
                            Opacity = 0.85,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    });
                    break;
                case ComposeMarkdownParser.Block.Code c:
                    PreviewHost.Children.Add(new Border
                    {
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(6),
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                        Child = new TextBlock
                        {
                            Text = c.Text,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    });
                    break;
                case ComposeMarkdownParser.Block.ListItem li:
                    PreviewHost.Children.Add(new TextBlock
                    {
                        Text = "• " + li.Text,
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
                case ComposeMarkdownParser.Block.Image img:
                {
                    var resolved = ResolvePreviewUrl(img.Url, baseUrl);
                    var panel = new StackPanel { Spacing = 4 };
                    if (resolved is not null)
                    {
                        panel.Children.Add(new CachedImage
                        {
                            SourceUrl = resolved,
                            MaxHeight = 220,
                            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Left
                        });
                    }
                    if (!string.IsNullOrEmpty(img.Alt) && img.Alt != "image")
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = img.Alt,
                            FontSize = 11,
                            Opacity = 0.6
                        });
                    }
                    PreviewHost.Children.Add(panel);
                    break;
                }
                case ComposeMarkdownParser.Block.LinkCard card:
                {
                    var cardBorder = new Border
                    {
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(8),
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(1)
                    };
                    var titleTb = new TextBlock
                    {
                        Text = card.Url.Host ?? card.Url.AbsoluteUri,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    };
                    var descTb = new TextBlock
                    {
                        Text = "正在获取预览…",
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
                    };
                    var thumb = new CachedImage
                    {
                        Width = 64,
                        Height = 64,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                        Visibility = Visibility.Collapsed
                    };
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(thumb);
                    var texts = new StackPanel { Spacing = 2 };
                    texts.Children.Add(titleTb);
                    texts.Children.Add(descTb);
                    Grid.SetColumn(texts, 1);
                    row.Children.Add(texts);
                    cardBorder.Child = row;
                    PreviewHost.Children.Add(cardBorder);

                    _ = EnrichPreviewLinkAsync(card.Url, titleTb, descTb, thumb, token);
                    break;
                }
                case ComposeMarkdownParser.Block.HorizontalRule:
                    PreviewHost.Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 6, 0, 6),
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
                    });
                    break;
            }
        }
    }

    private static string? ResolvePreviewUrl(string raw, Uri baseUrl)
    {
        raw = raw.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)) return abs.AbsoluteUri;
        try { return new Uri(baseUrl, raw).AbsoluteUri; }
        catch { return null; }
    }

    private static async Task EnrichPreviewLinkAsync(
        Uri url, TextBlock titleTb, TextBlock descTb, CachedImage thumb, CancellationToken token)
    {
        try
        {
            var preview = await OneboxService.Shared.PreviewAsync(url);
            if (token.IsCancellationRequested || preview is null) return;
            titleTb.DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                titleTb.Text = preview.Title;
                descTb.Text = preview.Description
                              ?? preview.SiteName
                              ?? url.AbsoluteUri;
                if (preview.ImageUrl is not null)
                {
                    thumb.SourceUrl = preview.ImageUrl.AbsoluteUri;
                    thumb.Visibility = Visibility.Visible;
                }
            });
        }
        catch
        {
            titleTb.DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested)
                    descTb.Text = url.AbsoluteUri;
            });
        }
    }

    private void WrapSelection(string prefix, string suffix)
    {
        var text = BodyBox.Text ?? "";
        var start = BodyBox.SelectionStart;
        var length = BodyBox.SelectionLength;
        if (length > 0 && start >= 0 && start + length <= text.Length)
        {
            var selected = text.Substring(start, length);
            BodyBox.Text = text[..start] + prefix + selected + suffix + text[(start + length)..];
            BodyBox.SelectionStart = start + prefix.Length;
            BodyBox.SelectionLength = selected.Length;
        }
        else
        {
            var insertAt = Math.Clamp(start, 0, text.Length);
            BodyBox.Text = text[..insertAt] + prefix + suffix + text[insertAt..];
            BodyBox.SelectionStart = insertAt + prefix.Length;
        }
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void InsertAtCursor(string value)
    {
        var text = BodyBox.Text ?? "";
        var at = Math.Clamp(BodyBox.SelectionStart, 0, text.Length);
        BodyBox.Text = text[..at] + value + text[at..];
        BodyBox.SelectionStart = at + value.Length;
        BodyBox.Focus(FocusState.Programmatic);
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewPane.Visibility == Visibility.Visible)
            _ = RefreshPreviewAsync();

        // Autosave draft (local always; server when enabled)
        _autosaveCts?.Cancel();
        _autosaveCts = new CancellationTokenSource();
        var token = _autosaveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1800, token);
                if (token.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!token.IsCancellationRequested)
                        await AutosaveAsync();
                });
            }
            catch { /* cancelled */ }
        }, token);

        // @mention detection
        var text = BodyBox.Text ?? "";
        var caret = BodyBox.SelectionStart;
        if (caret <= 0 || caret > text.Length)
        {
            MentionList.Visibility = Visibility.Collapsed;
            return;
        }
        var before = text[..caret];
        var at = before.LastIndexOf('@');
        if (at < 0 || (at > 0 && !char.IsWhiteSpace(before[at - 1]) && before[at - 1] != '\n'))
        {
            MentionList.Visibility = Visibility.Collapsed;
            return;
        }
        var query = before[(at + 1)..];
        if (query.Contains(' ') || query.Length > 20)
        {
            MentionList.Visibility = Visibility.Collapsed;
            return;
        }
        if (query.Length < 1)
        {
            MentionList.Visibility = Visibility.Collapsed;
            return;
        }

        _mentionCts?.Cancel();
        _mentionCts = new CancellationTokenSource();
        var mtoken = _mentionCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, mtoken);
                var users = await DiscourseAPI.Shared.SearchUsersAsync(query);
                if (mtoken.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    MentionList.ItemsSource = users.Select(u => u.Username).ToList();
                    MentionList.Visibility = users.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
            catch { /* ignore */ }
        }, mtoken);
    }

    private void MentionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string username) return;
        var text = BodyBox.Text ?? "";
        var caret = BodyBox.SelectionStart;
        if (caret <= 0 || caret > text.Length) return;
        var before = text[..caret];
        var at = before.LastIndexOf('@');
        if (at < 0) return;
        BodyBox.Text = text[..(at + 1)] + username + " " + text[caret..];
        BodyBox.SelectionStart = at + 1 + username.Length + 1;
        MentionList.Visibility = Visibility.Collapsed;
    }

    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            StatusText.Text = "上传图片中…";
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            var data = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer))
                reader.ReadBytes(data);
            var mime = file.FileType.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
            await UploadAndInsertAsync(data, file.Name, mime);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            StatusText.Text = "";
            APIError.PostIfChallenge(ex);
        }
    }

    private async void BodyBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            // Prefer bitmap paste
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                e.Handled = true;
                StatusText.Text = "正在上传剪贴板图片…";
                var streamRef = await content.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                var data = await ReadStreamAsync(stream);
                if (data.Length == 0)
                {
                    StatusText.Text = "剪贴板图片为空";
                    return;
                }
                var name = $"paste-{DateTimeOffset.Now.ToUnixTimeSeconds()}.png";
                await UploadAndInsertAsync(data, name, "image/png");
                return;
            }

            // Storage items (files copied)
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                var images = items
                    .OfType<Windows.Storage.StorageFile>()
                    .Where(f => IsImageExt(f.FileType))
                    .ToList();
                if (images.Count > 0)
                {
                    e.Handled = true;
                    foreach (var file in images)
                    {
                        StatusText.Text = $"上传 {file.Name}…";
                        var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
                        var data = new byte[buffer.Length];
                        using (var reader = DataReader.FromBuffer(buffer))
                            reader.ReadBytes(data);
                        var mime = MimeFromExt(file.FileType);
                        await UploadAndInsertAsync(data, file.Name, mime);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Fall through to default text paste if we can't handle image
            if (e.Handled)
            {
                ErrorText.Text = ex.Message;
                StatusText.Text = "";
                APIError.PostIfChallenge(ex);
            }
        }
    }

    private async Task UploadAndInsertAsync(byte[] data, string fileName, string mime)
    {
        if (!UserSessionStore.Current.IsLoggedIn)
        {
            ErrorText.Text = "请先登录后再上传图片";
            StatusText.Text = "";
            return;
        }
        StatusText.Text = "上传图片中…";
        ErrorText.Text = "";
        var upload = await DiscourseAPI.Shared.UploadImageDataAsync(data, fileName, mime);
        if (!string.IsNullOrEmpty(upload.Markdown))
            InsertAtCursor(upload.Markdown);
        else if (!string.IsNullOrEmpty(upload.Url))
            InsertAtCursor($"![{fileName}]({upload.Url})");
        StatusText.Text = "图片已插入";
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStreamWithContentType stream)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = (uint)stream.Size;
        await reader.LoadAsync(size);
        var data = new byte[size];
        reader.ReadBytes(data);
        return data;
    }

    private static bool IsImageExt(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    private static string MimeFromExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
