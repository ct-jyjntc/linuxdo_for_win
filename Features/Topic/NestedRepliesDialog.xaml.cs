using System.Collections.ObjectModel;
using LinuxDo.Core.Models;
using LinuxDo.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinuxDo.Features.Topic;

public sealed class NestedRowVm
{
    public NestedPostNode Node { get; init; } = new();
    public int Depth { get; init; }
    public bool IsExpanded { get; set; }
    public bool IsLoadingChildren { get; set; }
    public string Author => string.IsNullOrEmpty(Node.Username) ? "用户" : "@" + Node.Username;
    public string FloorLabel => $"#{Node.PostNumber}";
    public string Excerpt => Node.Excerpt;
    public string ReplyCountText => Node.ReplyCount > 0 ? $"{Node.ReplyCount} 回复" : "";
    public Visibility ShowExpand => Node.ReplyCount > 0 || IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ExpandGlyph => IsLoadingChildren ? "…" : IsExpanded ? "▼" : "▶";
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
}

public sealed partial class NestedRepliesDialog : ContentDialog
{
    private readonly int _topicId;
    private List<NestedPostNode> _roots = [];
    private readonly Dictionary<int, List<NestedPostNode>> _children = new();
    private readonly HashSet<int> _expanded = [];
    private readonly HashSet<int> _loading = [];
    private readonly ObservableCollection<NestedRowVm> _rows = [];

    public event Action<int>? OpenPost;

    public NestedRepliesDialog(int topicId, string title)
    {
        InitializeComponent();
        _topicId = topicId;
        Title = $"嵌套回复 · {title}";
        ReplyList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadRootsAsync();
    }

    private async void Refresh_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        LoadingRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            _roots = await DiscourseAPI.Shared.FetchNestedRootsAsync(_topicId);
            _children.Clear();
            _expanded.Clear();
            RebuildRows();
            if (_rows.Count == 0)
            {
                EmptyText.Text = "暂无嵌套数据。站点可能未启用 /n/topic 接口，请使用普通楼层视图。";
                EmptyText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            APIError.PostIfChallenge(ex);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void RebuildRows()
    {
        _rows.Clear();
        void Append(NestedPostNode node, int depth)
        {
            _rows.Add(new NestedRowVm
            {
                Node = node,
                Depth = depth,
                IsExpanded = _expanded.Contains(node.PostNumber),
                IsLoadingChildren = _loading.Contains(node.PostNumber)
            });
            if (_expanded.Contains(node.PostNumber) && _children.TryGetValue(node.PostNumber, out var kids))
            {
                foreach (var kid in kids)
                    Append(kid, depth + 1);
            }
        }
        foreach (var root in _roots)
            Append(root, 0);
    }

    private async void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not NestedRowVm row) return;
        var pn = row.Node.PostNumber;
        if (_expanded.Contains(pn))
        {
            _expanded.Remove(pn);
            RebuildRows();
            return;
        }
        _expanded.Add(pn);
        if (!_children.ContainsKey(pn))
        {
            _loading.Add(pn);
            RebuildRows();
            try
            {
                _children[pn] = await DiscourseAPI.Shared.FetchNestedChildrenAsync(_topicId, pn);
            }
            catch (Exception ex)
            {
                EmptyText.Text = ex.Message;
                EmptyText.Visibility = Visibility.Visible;
            }
            finally
            {
                _loading.Remove(pn);
            }
        }
        RebuildRows();
    }

    private void ReplyList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NestedRowVm row)
        {
            OpenPost?.Invoke(row.Node.PostNumber);
            Hide();
        }
    }
}
