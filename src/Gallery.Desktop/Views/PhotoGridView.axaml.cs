using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

/// <summary>Shows a <see cref="PhotoGridViewModel"/>: tells it the available width and scrolls where the model asks.</summary>
public partial class PhotoGridView : UserControl
{
    private PhotoGridViewModel? _vm;

    public PhotoGridView()
    {
        InitializeComponent();
        RowsList.SizeChanged += (_, e) => { if (e.WidthChanged) _vm?.SetWidth(e.NewSize.Width); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ScrollRequested -= OnScrollRequested;
            _vm.ScrollOffset = null;
        }
        _vm = DataContext as PhotoGridViewModel;
        if (_vm is null) return;
        _vm.ScrollRequested += OnScrollRequested;
        _vm.ScrollOffset = () => Scroller.Offset.Y - RowsList.Margin.Top;
        if (TopLevel.GetTopLevel(this) is { } top) _vm.RenderScaling = top.RenderScaling;
        if (RowsList.Bounds.Width > 0) _vm.SetWidth(RowsList.Bounds.Width);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null && TopLevel.GetTopLevel(this) is { } top) _vm.RenderScaling = top.RenderScaling;
    }

    /// <summary>Runs after the next layout, so the rows the model just built have been measured.</summary>
    private void OnScrollRequested(int index, bool toTop)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null || _vm.Columns == 0) return;
            var y = RowsList.Margin.Top + index / _vm.Columns * _vm.RowHeight;
            var view = Scroller.Viewport.Height;
            if (toTop) Scroller.Offset = new Vector(0, index == 0 ? 0 : y);
            else if (y < Scroller.Offset.Y) Scroller.Offset = new Vector(0, Math.Max(0, y - 12));
            else if (y + _vm.CellSize > Scroller.Offset.Y + view) Scroller.Offset = new Vector(0, y + _vm.CellSize - view + 12);
        }, DispatcherPriority.Loaded);
    }
}
