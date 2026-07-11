using LinuxDo.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace LinuxDo.Core.Utilities;

/// <summary>Wires global Ctrl+J / Ctrl+K / Ctrl+Enter list navigation to a ListView for the page lifetime.</summary>
public sealed class ListKeyboardNav : IDisposable
{
    private readonly ListView _list;
    private readonly Action<object>? _openItem;
    private bool _disposed;

    private ListKeyboardNav(ListView list, Action<object>? openItem)
    {
        _list = list;
        _openItem = openItem;
        if (_list.SelectionMode == ListViewSelectionMode.None)
            _list.SelectionMode = ListViewSelectionMode.Single;

        AppEvents.NavigateNext += OnNext;
        AppEvents.NavigatePrev += OnPrev;
        AppEvents.QuickAction += OnOpen;
    }

    public static ListKeyboardNav Attach(ListView list, Action<object>? openItem = null)
        => new(list, openItem);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppEvents.NavigateNext -= OnNext;
        AppEvents.NavigatePrev -= OnPrev;
        AppEvents.QuickAction -= OnOpen;
    }

    private void OnNext()
    {
        if (_list.Items.Count == 0) return;
        var idx = _list.SelectedIndex;
        if (idx < 0) idx = -1;
        var next = Math.Min(idx + 1, _list.Items.Count - 1);
        _list.SelectedIndex = next;
        if (_list.SelectedItem is not null)
            _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnPrev()
    {
        if (_list.Items.Count == 0) return;
        var idx = _list.SelectedIndex;
        if (idx < 0) idx = 0;
        var prev = Math.Max(idx - 1, 0);
        _list.SelectedIndex = prev;
        if (_list.SelectedItem is not null)
            _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnOpen()
    {
        if (_list.SelectedItem is null) return;
        if (_openItem is not null)
        {
            _openItem(_list.SelectedItem);
            return;
        }

        // Fallback: raise ItemClick-equivalent via common patterns is page-specific
    }
}
