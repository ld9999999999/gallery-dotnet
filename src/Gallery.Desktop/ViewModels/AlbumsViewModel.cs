using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>The "Albums" tab: the album list, and the photos of the album selected in it.</summary>
public sealed partial class AlbumsViewModel : ViewModelBase
{
    private readonly LibraryHost _library;
    private bool _refreshing;

    public AlbumsViewModel(LibraryHost library)
    {
        _library = library;
        Grid = new PhotoGridViewModel(library);
    }

    public PhotoGridViewModel Grid { get; }
    public ObservableCollection<AlbumEntryViewModel> Albums { get; } = new();

    [ObservableProperty] private AlbumEntryViewModel? _selected;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _message;

    partial void OnSelectedChanged(AlbumEntryViewModel? value)
    {
        if (_refreshing) return;
        if (value is null) { Grid.Clear(); return; }
        Grid.Filter.AlbumId = value.Album.Id;
        _ = Grid.ReloadAsync();
    }

    /// <summary>Reads the album list again, keeping the selection.</summary>
    public async Task RefreshAsync()
    {
        var (albums, covers) = await Task.Run(() =>
        {
            var all = _library.Albums.All();
            var items = _library.Items.GetItems(all.Where(a => a.CoverItemId is not null).Select(a => a.CoverItemId!.Value)).ToDictionary(i => i.Id);
            return (all, items);
        });
        var keep = Selected?.Album.Id;
        // Filling the list again clears the list box selection. Put it back without loading the photos again.
        _refreshing = true;
        try
        {
            foreach (var a in Albums) a.Release();
            Albums.Clear();
            foreach (var a in albums)
                Albums.Add(new AlbumEntryViewModel(_library, a, a.CoverItemId is { } id && covers.TryGetValue(id, out var cover) ? cover : null));
            IsEmpty = Albums.Count == 0;
            Selected = Albums.FirstOrDefault(a => a.Album.Id == keep) ?? Albums.FirstOrDefault();
        }
        finally { _refreshing = false; }
        if (Selected is null) { Grid.Clear(); return; }
        Grid.Filter.AlbumId = Selected.Album.Id;
        await Grid.ReloadAsync(keepPosition: Selected.Album.Id == keep);
    }

    /// <summary>Creates or updates albums from folder names, like the web gallery's "Auto" button.</summary>
    [RelayCommand]
    private async Task CreateFromFoldersAsync()
    {
        var r = await Task.Run(() => _library.Albums.BuildAutoAlbums(3));
        Message = $"Albums from folders: {r.Created:N0} created, {r.Updated:N0} updated, {r.ItemsAssigned:N0} items assigned"
            + (r.Unplaced == 0 ? "" : $"; {r.Unplaced:N0} left out, their folders generic (DCIM, RAW, a year…) with no named folder above: "
                + string.Join(", ", r.UnplacedFolders!.Take(3).Select(f => f.Folder)) + (r.UnplacedFolders!.Count > 3 ? "…" : ""));
        await RefreshAsync();
    }
}

/// <summary>An album in the list. Its cover is loaded while the entry is on screen.</summary>
public sealed partial class AlbumEntryViewModel : ObservableObject, IRealizable
{
    private const int CoverSize = 256;
    private readonly LibraryHost _library;
    private readonly ItemDto? _coverItem;
    private bool _realized, _held;

    public AlbumEntryViewModel(LibraryHost library, Album album, ItemDto? cover)
    {
        _library = library;
        _coverItem = cover;
        Album = album;
    }

    public Album Album { get; }
    public string Name => Album.Name;
    public string Detail => string.Join(" · ", new[] { Format.Count(Album.ItemCount, "item", "items"), Format.DateRange(Album.DateFrom, Album.DateTo) }.Where(s => s.Length > 0));
    public bool IsAuto => Album.Kind == "auto";

    [ObservableProperty] private Bitmap? _cover;

    public async void Realize()
    {
        if (_realized) return;
        _realized = true;
        if (_coverItem is not { PrimaryAssetId: { } assetId } item || item.Kind == MediaKind.Audio) return;
        var bitmap = await _library.Loader.AcquireAsync(assetId, item.Hash, item.Rotation, item.Squeeze, CoverSize);
        if (bitmap is null) return;
        if (!_realized) { _library.Loader.Release(item.Hash, item.Rotation, item.Squeeze, CoverSize); return; }
        _held = true;
        Cover = bitmap;
    }

    public void Release()
    {
        _realized = false;
        if (!_held || _coverItem is null) return;
        Cover = null;
        _held = false;
        _library.Loader.Release(_coverItem.Hash, _coverItem.Rotation, _coverItem.Squeeze, CoverSize);
    }
}
