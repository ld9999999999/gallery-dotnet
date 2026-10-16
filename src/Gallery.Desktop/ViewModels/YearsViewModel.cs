using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>The "Years" tab: the years photos were taken in, and the photos of the selected year.</summary>
public sealed partial class YearsViewModel : ViewModelBase
{
    private readonly LibraryHost _library;
    private bool _refreshing;

    public YearsViewModel(LibraryHost library)
    {
        _library = library;
        Grid = new PhotoGridViewModel(library);
    }

    public PhotoGridViewModel Grid { get; }
    public ObservableCollection<YearEntry> Years { get; } = new();

    [ObservableProperty] private YearEntry? _selected;
    [ObservableProperty] private bool _isEmpty;

    partial void OnSelectedChanged(YearEntry? value)
    {
        if (_refreshing) return;
        if (value is null) { Grid.Clear(); return; }
        Grid.Filter.Year = value.Year;
        _ = Grid.ReloadAsync();
    }

    public async Task RefreshAsync()
    {
        var years = await Task.Run(() => _library.Items.Years());
        var keep = Selected?.Year;
        // Refilling the list clears the list box's selection, so set it again without reloading the photos.
        _refreshing = true;
        try
        {
            Years.Clear();
            foreach (var (year, count) in years) Years.Add(new YearEntry(year, count));
            IsEmpty = Years.Count == 0;
            Selected = Years.FirstOrDefault(y => y.Year == keep) ?? Years.FirstOrDefault();
        }
        finally { _refreshing = false; }
        if (Selected is null) { Grid.Clear(); return; }
        Grid.Filter.Year = Selected.Year;
        await Grid.ReloadAsync(keepPosition: Selected.Year == keep);
    }
}

public sealed record YearEntry(int Year, long Count)
{
    public string Detail => Format.Count(Count, "item", "items");
}
