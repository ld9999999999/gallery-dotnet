using CommunityToolkit.Mvvm.ComponentModel;

namespace Gallery.Desktop.ViewModels;

/// <summary>One row of a <see cref="PhotoGridViewModel"/>. Its cells exist only while the row is on screen.</summary>
public sealed partial class PhotoRowViewModel : ObservableObject, IRealizable
{
    public PhotoRowViewModel(PhotoGridViewModel grid, int start, int count)
    {
        Grid = grid;
        Start = start;
        Count = count;
    }

    public PhotoGridViewModel Grid { get; }
    /// <summary>Index of the row's first item.</summary>
    public int Start { get; }
    public int Count { get; }
    public bool IsRealized { get; private set; }

    [ObservableProperty] private IReadOnlyList<PhotoCellViewModel> _cells = [];

    public void Realize()
    {
        if (IsRealized) return;
        IsRealized = true;
        Grid.OnRealized(this);
        Fill();
    }

    public void Release()
    {
        if (!IsRealized) return;
        IsRealized = false;
        Grid.OnReleased(this);
        foreach (var cell in Cells) cell.ReleaseThumb();
        Cells = [];
    }

    /// <summary>Gives each cell its item, asking the grid for the pages that are not loaded yet.</summary>
    internal void Fill()
    {
        if (!IsRealized) return;
        if (Cells.Count == 0)
        {
            var cells = new PhotoCellViewModel[Count];
            for (var k = 0; k < Count; k++) cells[k] = new PhotoCellViewModel(Grid, Start + k);
            Cells = cells;
        }
        var missing = false;
        foreach (var cell in Cells)
        {
            cell.Item ??= Grid.ItemAt(cell.Index);
            if (cell.Item is null) missing = true;
        }
        if (missing) Grid.RequestItems(Start, Start + Count - 1);
    }

    internal void ReloadThumbs()
    {
        foreach (var cell in Cells) { cell.ReleaseThumb(); cell.LoadThumb(); }
    }
}
