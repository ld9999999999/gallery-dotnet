using Avalonia;
using Avalonia.Controls;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

/// <summary>
/// Wraps the item template of a virtualized list. It tells the item (an <see cref="IRealizable"/>) when its view comes on
/// screen, and when it goes away or is reused for another item, so only the visible rows keep their thumbnails.
/// </summary>
public sealed class Realizer : Decorator
{
    private IRealizable? _current;
    private bool _attached;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Update();
    }

    private void Update()
    {
        var next = _attached ? DataContext as IRealizable : null;
        if (ReferenceEquals(next, _current)) return;
        _current?.Release();
        _current = next;
        _current?.Realize();
    }
}
