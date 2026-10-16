using CommunityToolkit.Mvvm.ComponentModel;
using Gallery.Core.Services;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// A virtual grid of items for one filter. It is a list of rows, and the list control creates only the rows near the
/// screen. A created row asks for its items, which are fetched <see cref="PageSize"/> at a time and dropped again when
/// far away. Its cells borrow their thumbnails from the library's <see cref="ThumbnailLoader"/>. However long the
/// result, only the rows on screen hold item data and decoded images, as in the web gallery.
/// </summary>
public sealed partial class PhotoGridViewModel : ViewModelBase
{
    public const int PageSize = 200;
    public const int KeepPages = 12;
    public const double Gap = 6;

    private readonly Dictionary<int, IReadOnlyList<ItemDto>> _pages = new();
    private readonly Dictionary<int, Task> _loading = new();
    private readonly HashSet<PhotoRowViewModel> _realized = new();
    private int _generation;
    private double _width;
    private int _pinned = -1; // the item the viewer shows: its page is kept

    public PhotoGridViewModel(LibraryHost library, ItemFilter? filter = null)
    {
        Library = library;
        Filter = filter ?? new ItemFilter();
    }

    public LibraryHost Library { get; }
    /// <summary>The query behind the grid; call <see cref="ReloadAsync"/> after changing it.</summary>
    public ItemFilter Filter { get; }

    [ObservableProperty] private long _total;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RowHeight))] private double _cellSize = 180;
    [ObservableProperty] private IReadOnlyList<PhotoRowViewModel> _rows = [];

    public double RowHeight => CellSize + Gap;
    public int Columns { get; private set; } = 1;
    public double ThumbSize { get; private set; } = 180;
    /// <summary>Device pixels per layout pixel, from the window.</summary>
    public double RenderScaling { get; set; } = 1;
    /// <summary>The thumbnail cache size to show: 512 once a cell is drawn larger than 256 device pixels.</summary>
    public int ThumbPixels => CellSize * RenderScaling > 256 ? 512 : 256;
    /// <summary>Rows with their view on screen (for tests and diagnostics).</summary>
    public int RealizedRows => _realized.Count;
    public int LoadedPages => _pages.Count;

    /// <summary>Called when a cell is clicked: the grid and the item's index.</summary>
    public Action<PhotoGridViewModel, int>? OpenRequested { get; set; }
    /// <summary>The view's current scroll offset in pixels, so a new layout can keep the first row on screen in place.</summary>
    public Func<double>? ScrollOffset { get; set; }
    /// <summary>Asks the view to scroll: the item index and whether to put its row at the top (else just bring it on screen).</summary>
    public event Action<int, bool>? ScrollRequested;

    public ItemDto? ItemAt(int index) =>
        index >= 0 && _pages.TryGetValue(index / PageSize, out var page) && index % PageSize < page.Count ? page[index % PageSize] : null;

    /// <summary>The item at an index, fetching its page when needed; null past the end.</summary>
    public async Task<ItemDto?> GetItemAsync(int index)
    {
        if (index < 0 || index >= Total) return null;
        if (ItemAt(index) is { } it) return it;
        await LoadPageAsync(index / PageSize);
        return ItemAt(index);
    }

    public int IndexOf(long itemId)
    {
        foreach (var (p, items) in _pages)
            for (var k = 0; k < items.Count; k++)
                if (items[k].Id == itemId) return p * PageSize + k;
        return -1;
    }

    /// <summary>Starts over with the current filter. keepPosition keeps the first row on screen (a refresh); otherwise back to the top.</summary>
    public async Task ReloadAsync(bool keepPosition = false)
    {
        var gen = ++_generation;
        var anchor = keepPosition ? Anchor() : 0;
        var query = Query(0);
        var (items, total) = await Task.Run(() => Library.Items.Query(query));
        if (gen != _generation) return;
        _pages.Clear();
        _loading.Clear();
        _pages[0] = items;
        Total = total;
        IsEmpty = total == 0;
        IsLoaded = true;
        BuildRows(Math.Min(anchor, (int)Math.Max(0, total - 1)));
    }

    /// <summary>Shows nothing (an album or year list with nothing selected).</summary>
    public void Clear()
    {
        _generation++;
        _pages.Clear();
        _loading.Clear();
        Total = 0;
        IsEmpty = true;
        IsLoaded = false;
        BuildRows(0);
    }

    /// <summary>Lays the grid out for the width available and the smallest thumbnail size, keeping the first row on screen in place.</summary>
    public void SetLayout(double width, double thumbSize)
    {
        if (width <= 0 || (width == _width && thumbSize == ThumbSize)) return;
        var anchor = Anchor();
        var pixels = ThumbPixels;
        _width = width;
        ThumbSize = thumbSize;
        var cols = Math.Max(1, (int)Math.Floor((width + Gap) / (thumbSize + Gap)));
        CellSize = Math.Max(1, Math.Floor((width - (cols - 1) * Gap) / cols));
        if (cols != Columns)
        {
            Columns = cols;
            BuildRows(anchor);
            return;
        }
        if (ThumbPixels != pixels) foreach (var row in _realized) row.ReloadThumbs();
        ScrollRequested?.Invoke(anchor, true);
    }

    /// <summary>The view's width changed.</summary>
    public void SetWidth(double width) => SetLayout(width, ThumbSize);

    /// <summary>The thumbnail size slider moved.</summary>
    public void SetThumbSize(double thumbSize)
    {
        if (_width > 0) SetLayout(_width, thumbSize);
        else ThumbSize = thumbSize;
    }

    /// <summary>Keeps the viewer's item loaded while it is open; -1 to stop.</summary>
    public void Pin(int index) => _pinned = index;

    /// <summary>The viewer closed on this index: bring it on screen.</summary>
    public void BringIntoView(int index) => ScrollRequested?.Invoke(index, false);

    /// <summary>An item's manual rotation changed: its cached data and thumbnail follow.</summary>
    public void OnItemRotated(long itemId, int rotation)
    {
        foreach (var p in _pages.Keys.ToList())
        {
            var items = _pages[p];
            for (var k = 0; k < items.Count; k++)
                if (items[k].Id == itemId)
                {
                    var copy = items.ToArray();
                    copy[k] = copy[k] with { Rotation = rotation };
                    _pages[p] = copy;
                }
        }
        foreach (var row in _realized)
            foreach (var cell in row.Cells)
                if (cell.Item?.Id == itemId) cell.Item = ItemAt(cell.Index);
    }

    internal void Open(int index) => OpenRequested?.Invoke(this, index);

    internal void OnRealized(PhotoRowViewModel row) => _realized.Add(row);

    internal void OnReleased(PhotoRowViewModel row) => _realized.Remove(row);

    /// <summary>Fetches the pages covering [first, last] that are not loaded; rows on screen fill in as pages arrive.</summary>
    internal void RequestItems(int first, int last)
    {
        for (var p = first / PageSize; p <= last / PageSize; p++) _ = LoadPageAsync(p);
    }

    private int Anchor() => ScrollOffset is { } offset && Rows.Count > 0 ? (int)Math.Floor(Math.Max(0, offset()) / RowHeight) * Columns : 0;

    private void BuildRows(int anchor)
    {
        var cols = Columns;
        var count = (int)((Total + cols - 1) / cols);
        var rows = new PhotoRowViewModel[count];
        for (var i = 0; i < count; i++) rows[i] = new PhotoRowViewModel(this, i * cols, (int)Math.Min(cols, Total - (long)i * cols));
        Rows = rows;
        ScrollRequested?.Invoke(anchor, true);
    }

    private ItemFilter Query(int offset)
    {
        var f = new ItemFilter
        {
            Q = Filter.Q, Kind = Filter.Kind, Year = Filter.Year, From = Filter.From, To = Filter.To, AlbumId = Filter.AlbumId,
            DuplicatesOnly = Filter.DuplicatesOnly, VariationsOnly = Filter.VariationsOnly, Untagged = Filter.Untagged,
            Sort = Filter.Sort, Offset = offset, Limit = PageSize,
        };
        f.Tags.AddRange(Filter.Tags);
        f.PersonIds.AddRange(Filter.PersonIds);
        return f;
    }

    private Task LoadPageAsync(int p)
    {
        if (_pages.ContainsKey(p)) return Task.CompletedTask;
        if (_loading.TryGetValue(p, out var running)) return running;
        var gen = _generation;
        var query = Query(p * PageSize);
        var task = LoadAsync();
        if (!task.IsCompleted) _loading[p] = task;
        return task;

        async Task LoadAsync()
        {
            try
            {
                var (items, total) = await Task.Run(() => Library.Items.Query(query));
                if (gen != _generation) return;
                _pages[p] = items;
                if (total != Total)
                {
                    // The library changed while loading (a scan is running): lay the rows out again around what is on screen.
                    Total = total;
                    IsEmpty = total == 0;
                    BuildRows(Anchor());
                    return;
                }
                foreach (var row in _realized.ToList())
                    if (row.Start < (p + 1) * PageSize && row.Start + row.Count > p * PageSize) row.Fill();
                EvictPages();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database went away (library closed) or failed; the cells stay empty.
            }
            finally
            {
                if (gen == _generation) _loading.Remove(p);
            }
        }
    }

    private void EvictPages()
    {
        if (_pages.Count <= KeepPages) return;
        var keep = new HashSet<int>();
        var first = int.MaxValue;
        foreach (var row in _realized)
        {
            first = Math.Min(first, row.Start);
            for (var p = row.Start / PageSize - 1; p <= (row.Start + row.Count - 1) / PageSize + 1; p++) keep.Add(p);
        }
        if (_pinned >= 0) { var q = _pinned / PageSize; keep.Add(q - 1); keep.Add(q); keep.Add(q + 1); }
        var centre = first == int.MaxValue ? 0 : first / PageSize;
        foreach (var p in _pages.Keys.OrderByDescending(k => Math.Abs(k - centre)).ToList())
        {
            if (_pages.Count <= KeepPages) break;
            if (!keep.Contains(p)) _pages.Remove(p);
        }
    }
}
