using CommunityToolkit.Mvvm.ComponentModel;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>The "All photos" tab: every item, with search, kind and sort filters.</summary>
public sealed partial class AllPhotosViewModel : ViewModelBase
{
    private CancellationTokenSource? _searchDelay;

    public AllPhotosViewModel(LibraryHost library)
    {
        Grid = new PhotoGridViewModel(library);
        _kind = Kinds[0];
        _sort = Sorts[0];
    }

    public PhotoGridViewModel Grid { get; }

    public IReadOnlyList<Choice> Kinds { get; } = [new("Everything", ""), new("Photos", "image"), new("Videos", "video"), new("Audio", "audio")];
    public IReadOnlyList<Choice> Sorts { get; } = [new("Newest first", "date_desc"), new("Oldest first", "date_asc"), new("Name", "name"), new("Recently added", "added")];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Choice _kind;
    [ObservableProperty] private Choice _sort;
    [ObservableProperty] private bool _duplicatesOnly;
    [ObservableProperty] private bool _variationsOnly;

    public Task LoadAsync() => Grid.ReloadAsync();

    partial void OnSearchChanged(string value)
    {
        // Wait for a pause in typing before searching.
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(_ =>
        {
            Grid.Filter.Q = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _ = Grid.ReloadAsync();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    partial void OnKindChanged(Choice value) { Grid.Filter.Kind = value.Value is "" ? null : value.Value; _ = Grid.ReloadAsync(); }
    partial void OnSortChanged(Choice value) { Grid.Filter.Sort = value.Value; _ = Grid.ReloadAsync(); }
    partial void OnDuplicatesOnlyChanged(bool value) { Grid.Filter.DuplicatesOnly = value; _ = Grid.ReloadAsync(); }
    partial void OnVariationsOnlyChanged(bool value) { Grid.Filter.VariationsOnly = value; _ = Grid.ReloadAsync(); }
}
