using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LinuxDo.Core.Utilities;

/// <summary>Captures/restores a ListView's vertical scroll offset around item mutations.</summary>
public static class ListScrollPreserver
{
    public sealed class Capture
    {
        public double VerticalOffset { get; init; }
        public object? AnchorItem { get; init; }
        public int AnchorIndex { get; init; } = -1;
    }

    public static Capture? Snapshot(ListView list)
    {
        try
        {
            var sv = FindScrollViewer(list);
            if (sv is null) return null;
            object? anchor = null;
            var index = -1;
            if (list.Items.Count > 0)
            {
                // Prefer first fully visible container
                for (var i = 0; i < list.Items.Count; i++)
                {
                    if (list.ContainerFromIndex(i) is ListViewItem container)
                    {
                        var transform = container.TransformToVisual(sv);
                        var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                        if (pt.Y >= -1)
                        {
                            anchor = list.Items[i];
                            index = i;
                            break;
                        }
                    }
                }
            }
            return new Capture
            {
                VerticalOffset = sv.VerticalOffset,
                AnchorItem = anchor,
                AnchorIndex = index
            };
        }
        catch
        {
            return null;
        }
    }

    public static async Task RestoreAsync(ListView list, Capture? capture, int settleMs = 80)
    {
        if (capture is null) return;
        try
        {
            await Task.Delay(settleMs);
            // Prefer re-scrolling to the same item if still present
            if (capture.AnchorItem is not null)
            {
                try
                {
                    list.ScrollIntoView(capture.AnchorItem, ScrollIntoViewAlignment.Leading);
                    await Task.Delay(16);
                }
                catch { /* fall through */ }
            }

            var sv = FindScrollViewer(list);
            if (sv is null) return;
            // If offset drifted significantly (e.g. prepend), put it back
            if (Math.Abs(sv.VerticalOffset - capture.VerticalOffset) > 2)
                sv.ChangeView(null, capture.VerticalOffset, null, disableAnimation: true);
        }
        catch
        {
            // ignore
        }
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}
