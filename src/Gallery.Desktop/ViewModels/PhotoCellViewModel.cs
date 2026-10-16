using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>One thumbnail in a grid row. Holds its bitmap only while its row is on screen.</summary>
public sealed partial class PhotoCellViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private (string Hash, int Rotation, double Squeeze, int Size)? _held;

    public PhotoCellViewModel(PhotoGridViewModel grid, int index)
    {
        Grid = grid;
        Index = index;
    }

    public PhotoGridViewModel Grid { get; }
    public int Index { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRaw), nameof(Variations), nameof(HasVariations), nameof(IsVideo), nameof(IsAudio), nameof(Duration), nameof(HasDuration), nameof(Label), nameof(IsEdited), nameof(Is360))]
    private ItemDto? _item;

    [ObservableProperty] private Bitmap? _thumb;

    public bool IsRaw => Item?.IsRaw == true;
    /// <summary>Shown as a 360° photo in the viewer.</summary>
    public bool Is360 => Item?.Projection is not null;
    /// <summary>Has settings saved in the photo editor.</summary>
    public bool IsEdited => Item?.Edited == true;
    public bool HasVariations => Item?.AssetCount > 1;
    public string Variations => Item?.AssetCount > 1 ? $"{Item.AssetCount}×" : "";
    public bool IsVideo => Item?.Kind == MediaKind.Video;
    public bool IsAudio => Item?.Kind == MediaKind.Audio;
    public bool HasDuration => Item?.DurationSec > 0;
    public string Duration => Item?.DurationSec is { } d ? Format.Duration(d) : "";
    public string Label => Item is null ? "" : $"{Item.Title} · {Item.TakenAt?[..Math.Min(10, Item.TakenAt.Length)]}";

    partial void OnItemChanged(ItemDto? value)
    {
        ReleaseThumb();
        LoadThumb();
    }

    /// <summary>Starts loading the thumbnail for the item, if there is one to show.</summary>
    public void LoadThumb()
    {
        var it = Item;
        if (it?.PrimaryAssetId is not { } assetId || string.IsNullOrEmpty(it.Hash) || it.Kind == MediaKind.Audio || _held is not null || _cts is not null) return;
        var size = Grid.ThumbPixels;
        var cts = _cts = new CancellationTokenSource();
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var bitmap = await Grid.Library.Loader.AcquireAsync(assetId, it.Hash, it.Rotation, it.Squeeze, size, cts.Token);
                if (bitmap is null) return;
                if (cts.IsCancellationRequested) { Grid.Library.Loader.Release(it.Hash, it.Rotation, it.Squeeze, size); return; }
                _held = (it.Hash, it.Rotation, it.Squeeze, size);
                Thumb = bitmap;
            }
            catch (OperationCanceledException) { }
            finally { if (_cts == cts) _cts = null; }
        }
    }

    /// <summary>Gives the bitmap back to the loader (the row left the screen) and stops a load in progress.</summary>
    public void ReleaseThumb()
    {
        _cts?.Cancel();
        _cts = null;
        if (_held is { } h)
        {
            Thumb = null;
            Grid.Library.Loader.Release(h.Hash, h.Rotation, h.Squeeze, h.Size);
            _held = null;
        }
    }

    [RelayCommand]
    private void Open() { if (Item is not null) Grid.Open(Index); }
}
