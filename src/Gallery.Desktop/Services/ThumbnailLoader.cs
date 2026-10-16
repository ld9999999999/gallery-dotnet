using Avalonia.Media.Imaging;
using Gallery.Core.Services;

namespace Gallery.Desktop.Services;

/// <summary>
/// Decoded thumbnails for the grids and album lists, so memory use depends on what is on screen, not on how far the
/// user has scrolled. All views showing a bitmap share it. When the last one releases it, it is kept among up to
/// <see cref="Capacity"/> unused bitmaps (so scrolling back is instant); past that, the least recently used are
/// disposed. Files come from the library's thumbnail cache and are made when missing.
/// Call only from the UI thread.
/// </summary>
public sealed class ThumbnailLoader
{
    private sealed class Entry(Bitmap bitmap)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public int Users;
        public LinkedListNode<string>? Unused;
    }

    private readonly ThumbnailService _thumbs;
    private readonly ItemRepository _items;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly LinkedList<string> _unused = new(); // least recently released first
    private readonly Dictionary<string, Task<bool>> _loading = new();

    public ThumbnailLoader(ThumbnailService thumbs, ItemRepository items)
    {
        _thumbs = thumbs;
        _items = items;
    }

    public int Capacity { get; set; } = 150;
    /// <summary>Number of decoded bitmaps held, in use or not.</summary>
    public int Count => _entries.Count;
    public int InUse => _entries.Values.Count(e => e.Users > 0);

    public static string Key(string hash, int rotation, int size, double squeeze = 1) => $"{hash}:{rotation}:{Squeeze.Format(squeeze)}:{size}";

    /// <summary>The thumbnail of an asset at a cache size (256, 512, …), or null when it cannot be shown. Pair with <see cref="Release"/>.</summary>
    public async Task<Bitmap?> AcquireAsync(long assetId, string hash, int rotation, int size, CancellationToken ct = default) =>
        await AcquireAsync(assetId, hash, rotation, 1, size, ct);

    /// <summary>Like the other overload, stretched by an anamorphic <paramref name="squeeze"/>.</summary>
    public async Task<Bitmap?> AcquireAsync(long assetId, string hash, int rotation, double squeeze, int size, CancellationToken ct = default)
    {
        var key = Key(hash, rotation, size, squeeze);
        if (!_entries.ContainsKey(key))
        {
            if (!_loading.TryGetValue(key, out var load)) _loading[key] = load = LoadAsync(key, assetId, hash, rotation, squeeze, size);
            await load.WaitAsync(ct); // if the caller cancels, the load keeps running and its bitmap goes to the unused ones
        }
        if (!_entries.TryGetValue(key, out var e)) return null;
        if (e.Unused is not null) { _unused.Remove(e.Unused); e.Unused = null; }
        e.Users++;
        return e.Bitmap;
    }

    public void Release(string hash, int rotation, int size) => Release(hash, rotation, 1, size);

    public void Release(string hash, int rotation, double squeeze, int size)
    {
        var key = Key(hash, rotation, size, squeeze);
        if (!_entries.TryGetValue(key, out var e) || e.Users == 0) return;
        if (--e.Users == 0) { e.Unused = _unused.AddLast(key); Trim(); }
    }

    private async Task<bool> LoadAsync(string key, long assetId, string hash, int rotation, double squeeze, int size)
    {
        try
        {
            var path = _thumbs.CachePath(hash, size, rotation, squeeze);
            if (!File.Exists(path))
            {
                var asset = await Task.Run(() => _items.GetAsset(assetId));
                if (asset is null) return false;
                path = await _thumbs.GetAsync(asset, size, rotation, squeeze);
                if (path is null) return false;
            }
            var bitmap = await Task.Run(() => { using var s = File.OpenRead(path); return new Bitmap(s); });
            var e = new Entry(bitmap);
            _entries[key] = e;
            e.Unused = _unused.AddLast(key);
            Trim();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            return false; // shown as an empty cell, like a broken thumbnail in the web gallery
        }
        finally { _loading.Remove(key); }
    }

    private void Trim()
    {
        while (_unused.Count > Capacity)
        {
            var key = _unused.First!.Value;
            _unused.RemoveFirst();
            if (_entries.Remove(key, out var e)) e.Bitmap.Dispose();
        }
    }
}
