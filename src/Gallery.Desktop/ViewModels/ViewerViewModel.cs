using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Faces;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The full-size viewer over a grid. It steps through the grid's items (and fetches pages not loaded yet), shows a
/// preview from the library's thumbnail service, turns photos (saved on the item, like an admin's rotation in the web
/// gallery), zooms photos from 50% to 200% of their own pixels, and opens videos and audio in the default player.
/// A 360° photo is shown from the middle of its sphere (<see cref="SphereRenderer"/>), as in the web gallery. It is
/// either a stitched photo, or two fisheye circles joined with the lens calibration in its EXIF and lined up by the
/// seam's distances.
/// </summary>
public sealed partial class ViewerViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _main;
    private ItemDetailDto? _detail;
    private int _generation;

    public ViewerViewModel(MainViewModel main, PhotoGridViewModel grid)
    {
        _main = main;
        Grid = grid;
        _showInfo = main.ShowViewerInfo;
        ZoomOptions = [new ZoomOption(0), .. ZoomLevels.Select(l => new ZoomOption(l))];
        _zoomLevel = ZoomLevels.Contains(main.Settings.ViewerZoom) ? main.Settings.ViewerZoom : 0;
        _selectedZoom = ZoomOptions.First(o => o.Level == _zoomLevel);
    }

    public PhotoGridViewModel Grid { get; }
    /// <summary>Preview size to render, from the window size; one of the thumbnail service's sizes.</summary>
    public int PreviewPixels { get; set; } = 2048;

    [ObservableProperty] private int _index;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _position = "";
    [ObservableProperty] private Bitmap? _image;
    /// <summary>Icon shown when there is no picture: "note" for audio, "play" for a video without a frame.</summary>
    [ObservableProperty] private string? _glyph;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _messageDetail;
    [ObservableProperty] private bool _showInfo;
    [ObservableProperty] private IReadOnlyList<InfoRow> _info = [];
    [ObservableProperty] private IReadOnlyList<InfoRow> _camera = [];
    /// <summary>
    /// The file's metadata in the info panel, as in the web gallery: a summary under "Camera &amp; EXIF" (or "Media info" for
    /// videos and audio), the location as a map link, and every tag of every group (EXIF, maker notes, GPS, XMP,
    /// IPTC… or the container's) under "All metadata". A filter narrows that list.
    /// </summary>
    [ObservableProperty] private string? _metadataHeading;
    [ObservableProperty] private string? _metadataEmpty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OpenLocationCommand))] private GeoPoint? _location;
    [ObservableProperty] private string? _locationText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AllMetadataLabel), nameof(HasAllMetadata))] private int _metadataTagCount;
    [ObservableProperty] private IReadOnlyList<MetadataGroupRow> _metadataGroups = [];
    [ObservableProperty] private string _metadataFilter = "";
    private List<MetaGroup> _allGroups = [];
    private long? _metadataAsset;
    public string AllMetadataLabel => $"All metadata ({MetadataTagCount:N0} tags)";
    public bool HasAllMetadata => MetadataTagCount > 0;
    public bool ShowAllMetadata
    {
        get => _main.ShowAllMetadata;
        set { if (_main.ShowAllMetadata == value) return; _main.ShowAllMetadata = value; OnPropertyChanged(); }
    }
    [ObservableProperty] private IReadOnlyList<VariationRow> _variations = [];
    [ObservableProperty] private IReadOnlyList<string> _tags = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OpenFileCommand), nameof(ShowInFolderCommand))] private AssetDto? _asset;
    [ObservableProperty] private bool _isPlayable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand))] private bool _canRotate;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EditCommand))] private bool _canEdit;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviousCommand))] private bool _hasPrevious;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool _hasNext;
    /// <summary>The faces in the photo, with boxes as fractions of the picture as shown.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFaces))] private IReadOnlyList<FaceInfo> _faces = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FacesToggleLabel))] private bool _showFaces;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedFace), nameof(SelectedFaceLabel), nameof(SelectedFaceHasPerson), nameof(AssignLabel))]
    private FaceInfo? _selectedFace;
    [ObservableProperty] private string _faceName = "";
    [ObservableProperty] private IReadOnlyList<PersonChip> _people = [];
    [ObservableProperty] private string? _unassignedFaces;
    /// <summary>The anamorphic factors offered for the item: none, the common ones, and the item's own if it is another.</summary>
    [ObservableProperty] private IReadOnlyList<SqueezeOption> _squeezeOptions = [];
    [ObservableProperty] private SqueezeOption? _selectedSqueeze;
    [ObservableProperty] private bool _canSqueeze;
    private bool _showingSqueeze;

    /// <summary>Showing the photo as a 360° view to look around in.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFlat))] private bool _isSphere;
    /// <summary>The 360° picture being looked at, while <see cref="IsSphere"/>.</summary>
    [ObservableProperty] private SphereRenderer? _sphere;
    /// <summary>Goes up when the 360° picture changes in place (the seam's distances arrived), so it is drawn again.</summary>
    [ObservableProperty] private int _sphereRevision;
    /// <summary>Where the 360° view looks: degrees to the right and up, and degrees across the window's longer side.</summary>
    [ObservableProperty] private double _yaw;
    [ObservableProperty] private double _pitch;
    [ObservableProperty] private double _fov = SphereRenderer.StartFov;
    /// <summary>The photo can be seen as a 360° view: a stitched one, or two fisheye circles whose lenses it describes.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleSphereCommand))] private bool _canSphere;
    /// <summary>A line over the 360° view: how to look around (the first time), or that a sharper picture is coming.</summary>
    [ObservableProperty] private string? _sphereNote;
    /// <summary>How the photo is shown, for everyone: as detected, as a 360° photo, or flat (the projection tag).</summary>
    [ObservableProperty] private IReadOnlyList<ProjectionOption> _projectionOptions = [];
    [ObservableProperty] private ProjectionOption? _selectedProjection;
    [ObservableProperty] private bool _canChooseProjection;
    private bool _showingProjection;
    /// <summary>The item shown flat by the 360°/flat switch, until the viewer moves on to another.</summary>
    private long? _flatItem;
    private double[]? _seam;
    private static bool _sphereHintShown;
    public bool IsFlat => !IsSphere;

    /// <summary>The zoom levels besides fit to window, as shares of the photo's own pixels (1: one per screen pixel).</summary>
    public static readonly double[] ZoomLevels = [0.5, 0.75, 1, 1.5, 2];
    /// <summary>The space kept around a photo fitted to the window, clear of the buttons at the top.</summary>
    public static readonly Thickness FitMargin = new(16, 56, 16, 16);
    private (double Width, double Height, double Scaling) _stage = (1200, 800, 1);
    private int _pictures;
    private int _sharperSize;
    private bool _showingZoom;

    /// <summary>0: the photo fitted to the window; otherwise one of <see cref="ZoomLevels"/>. Remembered in the settings.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsZoomed))] private double _zoomLevel;
    /// <summary>A photo with a preview on screen, which can be zoomed (not a video or audio).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsZoomed))]
    [NotifyCanExecuteChangedFor(nameof(ToggleZoomCommand), nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(FitCommand))]
    private bool _canZoom;
    /// <summary>The photo's own size in pixels, in the shape of the preview (turned and stretched as shown); 0 without one.</summary>
    [ObservableProperty] private double _fullWidth;
    [ObservableProperty] private double _fullHeight;
    /// <summary>How the photo is laid out on screen; the view follows it.</summary>
    [ObservableProperty] private ZoomView _view;
    /// <summary>The size a fitted photo is shown at, as a percentage of its own pixels (0 without a photo).</summary>
    [ObservableProperty] private int _fitPercent;
    [ObservableProperty] private ZoomOption? _selectedZoom;

    /// <summary>The zoom menu: Fit (with its own percentage), then <see cref="ZoomLevels"/>.</summary>
    public IReadOnlyList<ZoomOption> ZoomOptions { get; }
    public bool IsZoomed => CanZoom && ZoomLevel > 0;

    public bool HasFaces => Faces.Count > 0;
    public bool HasSelectedFace => SelectedFace is not null;
    public bool SelectedFaceHasPerson => SelectedFace?.PersonId is not null;
    public string SelectedFaceLabel => SelectedFace is null ? "" : SelectedFace.PersonName ?? (SelectedFace.PersonId is null ? "Nobody yet" : "Unnamed person");
    public string AssignLabel => SelectedFace?.PersonId is null ? "Name" : "Move";
    public string FacesToggleLabel => ShowFaces ? "Hide faces" : "Show faces (F)";

    private bool HasFile => Asset is { Missing: false };

    public async Task ShowAsync(int index)
    {
        var item = await Grid.GetItemAsync(index);
        if (item is null) return;
        var gen = ++_generation;
        Index = index;
        Grid.Pin(index);
        Title = item.Title;
        Position = $"{index + 1:N0} of {Grid.Total:N0}";
        HasPrevious = index > 0;
        HasNext = index + 1 < Grid.Total;
        var lib = Grid.Library;
        // Like opening an item in the web gallery: check its files first, so a deleted one says so.
        var detail = await Task.Run(() => { lib.Changes.CheckItem(item.Id); return lib.Items.Get(item.Id); });
        if (gen != _generation) return;
        _detail = detail;
        if (detail is null)
        {
            ShowPicture(null, null, null);
            Asset = null;
            Message = "This item is no longer in the library";
            MessageDetail = null;
            return;
        }
        Tags = detail.Tags.Where(t => t.Key != Rotation.TagKey && t.Key != Squeeze.TagKey && t.Key != Projection.TagKey)
            .Select(t => $"{t.Key}: {t.Value}").ToList(); // these have their own controls
        if (_flatItem != item.Id) _flatItem = null;
        var asset = detail.Assets.FirstOrDefault(a => a.Id == detail.Item.PrimaryAssetId && !a.Missing) ?? detail.Assets.FirstOrDefault(a => !a.Missing) ?? detail.Assets.FirstOrDefault();
        await ShowAssetAsync(asset, gen);
    }

    private async Task ShowAssetAsync(AssetDto? asset, int gen)
    {
        var detail = _detail!;
        Asset = asset;
        var deleted = detail.Assets.Count > 0 && detail.Assets.All(a => a.Missing);
        IsPlayable = asset is { Missing: false } && asset.Kind is MediaKind.Video or MediaKind.Audio;
        CanRotate = asset is { Missing: false } && asset.Kind != MediaKind.Audio;
        CanEdit = EditTarget() is not null && !deleted;
        Info = asset is null ? [] : InfoRows(detail.Item, asset);
        Variations = detail.Assets.Select(a => new VariationRow(a, a.Id == detail.Item.PrimaryAssetId, a.Id == asset?.Id)).ToList();
        ShowSqueeze(detail.Item);
        ShowProjection(detail, asset);
        ClearMetadata();
        Glyph = null;
        ShowSphere(null);
        CanSphere = false;
        if (asset is null || deleted)
        {
            ShowPicture(null, asset, detail.Item);
            Message = asset is null ? "No files" : "File deleted";
            MessageDetail = deleted ? $"{(detail.Assets.Count > 1 ? "These files are" : "This file is")} no longer on disk, so the item has left the gallery." : null;
            return;
        }
        Message = null;
        MessageDetail = null;
        IsLoading = true;
        var lib = Grid.Library;
        var rotation = detail.Item.Rotation;
        var lenses = asset.Kind == MediaKind.Image && asset.Projection == Projection.DualFisheye ? await Task.Run(() => Lenses(detail, asset)) : null;
        if (gen != _generation) return;
        CanSphere = asset.Kind == MediaKind.Image && (asset.Projection == Projection.Equirectangular || lenses is not null);
        if (CanSphere && _flatItem != detail.Item.Id) { await ShowSphereAsync(detail, asset, lenses, gen); return; }
        Bitmap? bitmap = null;
        try
        {
            var entity = await Task.Run(() => lib.Items.GetAsset(asset.Id));
            var path = entity is null ? null : await lib.Thumbnails.GetAsync(entity, PreviewPixels, rotation, detail.Item.Squeeze);
            if (path is not null) bitmap = await Task.Run(() => { using var s = File.OpenRead(path); return new Bitmap(s); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        if (gen != _generation) { bitmap?.Dispose(); return; }
        IsLoading = false;
        ShowPicture(bitmap, asset, detail.Item);
        _ = SharpenAsync(gen);
        if (bitmap is null) Glyph = asset.Kind switch { MediaKind.Audio => "note", MediaKind.Video => "play", _ => null };
        if (bitmap is null && asset.Kind == MediaKind.Image) { Message = "No preview"; MessageDetail = "This file could not be rendered."; }
        await LoadFacesAsync(asset.Kind == MediaKind.Image && bitmap is not null ? detail.Item.Id : null, gen);
        if (ShowInfo) await LoadCameraAsync(asset, gen);
    }

    /// <summary>
    /// For two fisheye circles: their lenses, read from the file's own EXIF. An exported edit covers the whole frame of its
    /// original (maybe smaller), so the lenses sit where they do in the original.
    /// </summary>
    private static DualFisheye.Rig? Lenses(ItemDetailDto detail, AssetDto asset)
    {
        var frame = asset.EditOf is { } of ? detail.Assets.FirstOrDefault(x => x.Id == of) ?? asset : asset;
        return frame is { Width: { } w, Height: { } h } ? DualFisheye.Read(asset.Path, w, h) : null;
    }

    /// <summary>
    /// The 360° view of a photo, looking straight ahead. 1. Show the 2048 pixel preview at once (often there already).
    /// 2. Load a sharper one, since the view shows only a small part of the sphere. 3. For fisheye circles, load the
    /// seam's distances too.
    /// </summary>
    private async Task ShowSphereAsync(ItemDetailDto detail, AssetDto asset, DualFisheye.Rig? lenses, int gen)
    {
        var lib = Grid.Library;
        SphereRenderer? sphere = null;
        Asset? entity = null;
        try
        {
            entity = await Task.Run(() => lib.Items.GetAsset(asset.Id));
            var path = entity is null ? null : await lib.Thumbnails.GetAsync(entity, 2048, detail.Item.Rotation, detail.Item.Squeeze);
            if (path is not null) sphere = await Task.Run(() => SphereRenderer.Load(path, lenses));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SixLabors.ImageSharp.ImageFormatException) { }
        if (gen != _generation) return;
        IsLoading = false;
        ShowPicture(null, asset, detail.Item);
        await LoadFacesAsync(null, gen); // face boxes belong to the flat picture
        if (sphere is null || entity is null) { Message = "No preview"; MessageDetail = "This file could not be rendered."; return; }
        _seam = null;
        Yaw = Pitch = 0;
        Fov = SphereRenderer.StartFov;
        ShowSphere(sphere);
        if (!_sphereHintShown) { _sphereHintShown = true; _ = NoteAsync("Drag to look around · scroll or pinch to zoom · double-click to reset", 5000, gen); }
        if (lenses is not null) _ = LoadSeamAsync(entity, gen);
        _ = SharpenSphereAsync(detail, entity, lenses, gen);
        if (ShowInfo) await LoadCameraAsync(asset, gen);
    }

    private void ShowSphere(SphereRenderer? sphere)
    {
        Sphere = sphere;
        IsSphere = sphere is not null;
        if (sphere is null) SphereNote = null;
    }

    private async Task NoteAsync(string text, int ms, int gen)
    {
        SphereNote = text;
        await Task.Delay(ms);
        if (gen == _generation && SphereNote == text) SphereNote = null;
    }

    /// <summary>
    /// The preview worth loading after the 2048 one: enough for the window at the starting field of view, up to 8192
    /// pixels across (128 MB as a texture) and never more than the photo has.
    /// </summary>
    private async Task SharpenSphereAsync(ItemDetailDto detail, Asset entity, DualFisheye.Rig? lenses, int gen)
    {
        var screen = Math.Max(_stage.Width, _stage.Height) * _stage.Scaling;
        var want = Math.Min(entity.Width ?? int.MaxValue, screen * 360 / SphereRenderer.StartFov);
        var size = 2048;
        foreach (var s in new[] { 4096, 8192 }) if (size < want) size = s;
        if (size <= 2048) return;
        SphereNote ??= "Loading the full picture…";
        SphereRenderer? sharper = null;
        try
        {
            var lib = Grid.Library;
            var path = await lib.Thumbnails.GetAsync(entity, size, detail.Item.Rotation, detail.Item.Squeeze, panorama: true);
            if (path is not null) sharper = await Task.Run(() => SphereRenderer.Load(path, lenses));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SixLabors.ImageSharp.ImageFormatException) { }
        if (SphereNote == "Loading the full picture…") SphereNote = null;
        if (gen != _generation || sharper is null || Sphere is not { } now || sharper.TextureWidth <= now.TextureWidth) return;
        sharper.Seam = _seam;
        ShowSphere(sharper);
    }

    /// <summary>How far away things are along the seam of two fisheye circles, worked out the first time (about a second).</summary>
    private async Task LoadSeamAsync(Asset entity, int gen)
    {
        double[]? seam = null;
        try { seam = await Grid.Library.Seams.GetAsync(entity); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        if (gen != _generation || seam is null || Sphere is not { IsFisheye: true } sphere) return;
        _seam = seam;
        sphere.Seam = seam;
        SphereRevision++;
    }

    /// <summary>V: the 360° view, or the photo flat (the whole picture at once), for this item until the viewer moves on.</summary>
    [RelayCommand(CanExecute = nameof(CanSphere))]
    private async Task ToggleSphereAsync()
    {
        if (_detail is not { } d || Asset is not { } asset) return;
        _flatItem = IsSphere ? d.Item.Id : null;
        await ShowAssetAsync(asset, ++_generation);
    }

    /// <summary>Turns the 360° view by degrees: to the right, and up (to straight up or down at most).</summary>
    public void Turn(double right, double up)
    {
        Yaw = ((Yaw + right) % 360 + 540) % 360 - 180;
        Pitch = Math.Clamp(Pitch + up, -90, 90);
    }

    public void ZoomSphereTo(double fov) => Fov = Math.Clamp(fov, SphereRenderer.MinFov, SphereRenderer.MaxFov);

    public void ResetSphere()
    {
        Yaw = Pitch = 0;
        Fov = SphereRenderer.StartFov;
    }

    /// <summary>
    /// Keys in the 360° view: ↑ ↓ look up and down, Shift+← → turn (← → alone still step between items), + − zoom, 0 back to
    /// the start. Returns whether the key was one of them.
    /// </summary>
    public bool SphereKey(Avalonia.Input.Key key, bool shift)
    {
        if (!IsSphere) return false;
        var step = Fov / 15;
        switch (key)
        {
            case Avalonia.Input.Key.Up: Turn(0, step); return true;
            case Avalonia.Input.Key.Down: Turn(0, -step); return true;
            case Avalonia.Input.Key.Left when shift: Turn(-step, 0); return true;
            case Avalonia.Input.Key.Right when shift: Turn(step, 0); return true;
            case Avalonia.Input.Key.OemPlus or Avalonia.Input.Key.Add: ZoomSphereTo(Fov / 1.15); return true;
            case Avalonia.Input.Key.OemMinus or Avalonia.Input.Key.Subtract: ZoomSphereTo(Fov * 1.15); return true;
            case Avalonia.Input.Key.D0 or Avalonia.Input.Key.NumPad0: ResetSphere(); return true;
            default: return false;
        }
    }

    /// <summary>The 360° choice in the info panel: offered for photos that are, were detected as, or could be 360° ones (twice as wide as high).</summary>
    private void ShowProjection(ItemDetailDto detail, AssetDto? asset)
    {
        _showingProjection = true;
        try
        {
            var tagged = detail.Tags.FirstOrDefault(t => t.Key == Projection.TagKey)?.Value;
            var sphere = asset?.Projection is not null;
            var detected = asset?.DetectedProjection is not null;
            CanChooseProjection = asset is { Kind: MediaKind.Image } a && (sphere || detected || tagged is not null || (a.Width is > 0 && a.Width == 2 * a.Height));
            ProjectionOptions = [new(null, $"Automatic ({(detected ? "360°" : "flat")})"), new(true, "360° photo"), new(false, "Flat photo")];
            SelectedProjection = ProjectionOptions.First(o => o.Sphere == (tagged is null ? null : tagged == Projection.TagSphere));
        }
        finally { _showingProjection = false; }
    }

    partial void OnSelectedProjectionChanged(ProjectionOption? value)
    {
        if (_showingProjection || value is null || _detail is not { } d) return;
        _ = SetProjectionAsync(d.Item.Id, value.Sphere);
    }

    /// <summary>Says how the item is shown, for everyone (null: as detected); the grid's badge follows.</summary>
    private async Task SetProjectionAsync(long itemId, bool? sphere)
    {
        var lib = Grid.Library;
        await Task.Run(() => lib.Items.SetProjection(itemId, sphere));
        _flatItem = null;
        await Grid.ReloadAsync(keepPosition: true);
        await ShowAsync(Index);
    }

    private static List<InfoRow> InfoRows(ItemDto item, AssetDto a)
    {
        var rows = new List<InfoRow>();
        var source = a.TakenSource switch { "filename" => " · from file name", "file" => " · file date", _ => "" };
        rows.Add(new("Taken", Format.Date(a.TakenAt ?? item.TakenAt) + source));
        rows.Add(new("Folder", item.DirName));
        if (a.Width is { } w && a.Height is { } h) rows.Add(new("Size", $"{w} × {h}{(a.DurationSec is { } d ? " · " + Format.Duration(d) : "")}"));
        else if (a.DurationSec is { } d2) rows.Add(new("Length", Format.Duration(d2)));
        if (!string.IsNullOrEmpty(a.Codec)) rows.Add(new("Codec", a.Codec + (string.IsNullOrEmpty(a.AudioCodec) ? "" : " / " + a.AudioCodec)));
        rows.Add(new("File", $"{Format.Bytes(a.Size)} · {a.Ext.ToUpperInvariant()}"));
        return rows;
    }

    private void ClearMetadata()
    {
        Camera = [];
        MetadataHeading = MetadataEmpty = LocationText = null;
        Location = null;
        _allGroups = [];
        MetadataGroups = [];
        MetadataTagCount = 0;
        _metadataAsset = null;
        MetadataFilter = ""; // a new file starts unfiltered; the section stays open or closed as it was
    }

    /// <summary>The file's metadata, read from it when the info panel is open (not stored).</summary>
    private async Task LoadCameraAsync(AssetDto asset, int gen)
    {
        if (_metadataAsset == asset.Id) return;
        var lib = Grid.Library;
        AssetMetadata? meta;
        try { meta = await Task.Run(async () => lib.Items.GetAsset(asset.Id) is { } e ? await lib.Metadata.ReadDetailsAsync(e) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { meta = null; }
        if (gen != _generation || meta is null) return;
        _metadataAsset = asset.Id;
        var image = asset.Kind == MediaKind.Image;
        MetadataHeading = image ? "Camera & EXIF" : "Media info";
        Camera = meta.Summary.Select(f => new InfoRow(f.Label, f.Value)).ToList();
        Location = meta.Location;
        LocationText = meta.Location is { } g ? $"{g.Lat:0.00000}, {g.Lon:0.00000}{(g.AltitudeM is { } alt ? $" · {Math.Round(alt)} m" : "")}" : null;
        MetadataEmpty = Camera.Count == 0 && Location is null ? image ? "No camera metadata in this file" : "No details available" : null;
        _allGroups = meta.Groups;
        MetadataTagCount = meta.Groups.Sum(gr => gr.Tags.Count);
        FilterMetadata();
    }

    partial void OnMetadataFilterChanged(string value) => FilterMetadata();

    /// <summary>The groups with the tags whose name or value holds the filter's text (all without one); empty groups are dropped.</summary>
    private void FilterMetadata()
    {
        var q = MetadataFilter.Trim();
        MetadataGroups = _allGroups
            .Select(g => new MetadataGroupRow(g.Name, g.Tags.Count,
                q.Length == 0 ? g.Tags : g.Tags.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Value.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(g => g.Tags.Count > 0).ToList();
    }

    /// <summary>The photo's location on OpenStreetMap, in the browser.</summary>
    [RelayCommand(CanExecute = nameof(HasLocation))]
    private Task OpenLocationAsync() => Location is { } g && _main.Shell is { } shell
        ? shell.OpenUrlAsync(new Uri(FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={g.Lat}&mlon={g.Lon}#map=15/{g.Lat}/{g.Lon}")))
        : Task.CompletedTask;

    private bool HasLocation() => Location is not null;

    private async Task LoadFacesAsync(long? itemId, int gen)
    {
        var lib = Grid.Library;
        var faces = itemId is { } id ? await Task.Run(() => lib.People.ItemFaces(id, forViewers: false) ?? []) : [];
        if (gen != _generation) return;
        Faces = faces;
        SelectedFace = null;
        People = faces.Where(f => f.PersonId is not null).GroupBy(f => f.PersonId!.Value)
            .Select(g => new PersonChip(g.Key, g.First().PersonName ?? "Unnamed person")).ToList();
        var nobody = faces.Count(f => f.PersonId is null);
        UnassignedFaces = nobody == 0 ? null : $"{Format.Count(nobody, "face", "faces")} nobody is assigned to";
    }

    [RelayCommand]
    private void ToggleFaces() => ShowFaces = !ShowFaces;

    /// <summary>A face clicked in the photo: shown in the info panel, where it can be named or corrected.</summary>
    [RelayCommand]
    private void SelectFace(FaceInfo? face)
    {
        SelectedFace = face;
        FaceName = face?.PersonName ?? "";
        if (face is not null && !ShowInfo) { ShowInfo = true; _main.ShowViewerInfo = true; }
    }

    [RelayCommand]
    private async Task AssignFaceAsync()
    {
        if (SelectedFace is not { } face || _detail is not { } d) return;
        var name = FaceName.Trim();
        if (name.Length == 0 || name == face.PersonName) return;
        var lib = Grid.Library;
        await Task.Run(() => lib.People.Assign([face.Id], null, name));
        await ReloadFacesAsync(d.Item.Id, face.Id);
    }

    [RelayCommand]
    private async Task NotThisPersonAsync()
    {
        if (SelectedFace is not { PersonId: not null } face || _detail is not { } d) return;
        var lib = Grid.Library;
        await Task.Run(() => lib.People.Unassign([face.Id]));
        await ReloadFacesAsync(d.Item.Id, face.Id);
    }

    private async Task ReloadFacesAsync(long itemId, long keepFace)
    {
        await LoadFacesAsync(itemId, _generation);
        SelectFace(Faces.FirstOrDefault(f => f.Id == keepFace));
    }

    [RelayCommand]
    private Task OpenPersonAsync(PersonChip person) => _main.OpenPersonAsync(person.Id);

    private void SetImage(Bitmap? bitmap)
    {
        var old = Image;
        Image = bitmap;
        // Let the picture on screen be replaced before its bitmap goes.
        if (old is not null && old != bitmap) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }

    /// <summary>
    /// A new picture on screen: its own size in pixels is the file's long side, stretched by the item's anamorphic squeeze,
    /// in the shape of the preview (which is turned and stretched as shown). The view centres it at the zoom level.
    /// </summary>
    private void ShowPicture(Bitmap? bitmap, AssetDto? asset, ItemDto? item)
    {
        SetImage(bitmap);
        _sharperSize = 0;
        _pictures++;
        if (bitmap is { PixelSize: { Width: > 0, Height: > 0 } px })
        {
            var f = asset is { Width: > 0, Height: > 0 } a ? Math.Max(a.Width!.Value, a.Height!.Value) * Squeeze.Normalize(item?.Squeeze ?? 1) / Math.Max(px.Width, px.Height) : 1;
            FullWidth = px.Width * f;
            FullHeight = px.Height * f;
        }
        else FullWidth = FullHeight = 0;
        CanZoom = asset?.Kind == MediaKind.Image && bitmap is not null;
        UpdateView();
    }

    /// <summary>The size of the area the photo is shown in (device-independent) and the screen pixels per unit of it.</summary>
    public void SetStage(double width, double height, double scaling)
    {
        if (width < 1 || height < 1 || scaling <= 0 || _stage == (width, height, scaling)) return;
        _stage = (width, height, scaling);
        UpdateView();
    }

    private void UpdateView()
    {
        var s = _stage.Scaling;
        View = IsZoomed && FullWidth > 0 ? new ZoomView(true, FullWidth * ZoomLevel / s, FullHeight * ZoomLevel / s, ZoomLevel >= 2, _pictures) : new ZoomView(false, 0, 0, false, _pictures);
        double w = _stage.Width - FitMargin.Left - FitMargin.Right, h = _stage.Height - FitMargin.Top - FitMargin.Bottom;
        FitPercent = FullWidth > 0 && w > 0 && h > 0 ? (int)Math.Round(Math.Min(w / FullWidth, h / FullHeight) * s * 100) : 0;
        ZoomOptions[0].Label = FitPercent > 0 ? $"Fit · {FitPercent}%" : "Fit";
    }

    /// <summary>Sets the zoom level (0: fit), which is remembered for the photos opened next.</summary>
    public void SetZoom(double level)
    {
        if (CanZoom) ZoomLevel = ZoomLevels.Contains(level) ? level : 0;
    }

    /// <summary>The level a step in (1) or out (-1) from the one on screen goes to.</summary>
    public double NextLevel(int step)
    {
        double[] levels = [0, .. ZoomLevels];
        return levels[Math.Clamp(Array.IndexOf(levels, ZoomLevel) + step, 0, levels.Length - 1)];
    }

    partial void OnZoomLevelChanged(double value)
    {
        _main.Settings.ViewerZoom = value;
        _main.SaveSettings();
        _showingZoom = true;
        try { SelectedZoom = ZoomOptions.First(o => o.Level == value); }
        finally { _showingZoom = false; }
        UpdateView();
        _ = SharpenAsync(_generation);
    }

    partial void OnSelectedZoomChanged(ZoomOption? value)
    {
        if (!_showingZoom && value is not null) SetZoom(value.Level);
    }

    /// <summary>Z: fit, or 100%.</summary>
    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ToggleZoom() => SetZoom(ZoomLevel > 0 ? 0 : 1);

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomIn() => SetZoom(NextLevel(1));

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomOut() => SetZoom(NextLevel(-1));

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void Fit() => SetZoom(0);

    /// <summary>
    /// Swaps in a larger preview (4096 or 8192 pixels, never larger than the photo) when the zoom level shows more detail
    /// than the one on screen has. It has the same shape, so the layout stays as it is.
    /// </summary>
    private async Task SharpenAsync(int gen)
    {
        if (!IsZoomed || Image is not { } shown || _detail is not { } d || Asset is not { } asset) return;
        var need = Math.Max(FullWidth, FullHeight) * Math.Min(ZoomLevel, 1); // beyond 100% the photo's own pixels are enlarged
        var have = Math.Max(shown.PixelSize.Width, shown.PixelSize.Height);
        if (need <= have * 1.05) return;
        var size = need <= 2048 ? 2048 : need <= 4096 ? 4096 : 8192;
        if (size <= _sharperSize || size <= have) return; // asked for already
        _sharperSize = size;
        var lib = Grid.Library;
        Bitmap? bitmap = null;
        try
        {
            var entity = await Task.Run(() => lib.Items.GetAsset(asset.Id));
            var path = entity is null ? null : await lib.Thumbnails.GetAsync(entity, size, d.Item.Rotation, d.Item.Squeeze, panorama: size > ThumbnailService.AllowedSizes[^1]);
            if (path is not null) bitmap = await Task.Run(() => { using var s = File.OpenRead(path); return new Bitmap(s); });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        // Another photo since, or a larger preview already in (two levels asked for in a row): keep what is on screen.
        if (gen != _generation || bitmap is null || Image is not { } now || bitmap.PixelSize.Width <= now.PixelSize.Width) { bitmap?.Dispose(); return; }
        SetImage(bitmap);
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private Task PreviousAsync() => ShowAsync(Index - 1);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private Task NextAsync() => ShowAsync(Index + 1);

    [RelayCommand]
    private void Close() => _main.CloseViewer();

    [RelayCommand(CanExecute = nameof(CanRotate))]
    private Task RotateLeftAsync() => RotateAsync(-90);

    [RelayCommand(CanExecute = nameof(CanRotate))]
    private Task RotateRightAsync() => RotateAsync(90);

    /// <summary>Saves a quarter turn on the item (the rotate-degrees tag) and shows the turned rendering.</summary>
    private async Task RotateAsync(int by)
    {
        if (_detail is not { } detail || Asset is not { } asset) return;
        var id = detail.Item.Id;
        var lib = Grid.Library;
        var rotation = await Task.Run(() => lib.Items.RotateItem(id, by));
        if (rotation is null || _detail?.Item.Id != id) return;
        _detail = detail with { Item = detail.Item with { Rotation = rotation.Value } };
        Grid.OnItemRotated(id, rotation.Value);
        await ShowAssetAsync(asset, ++_generation);
    }

    /// <summary>
    /// The file the editor opens: the item's RAW when it has one (the most to work with), the original of an export on
    /// screen, otherwise the photo on screen.
    /// </summary>
    private AssetDto? EditTarget()
    {
        if (_detail is not { } d || Asset is not { Kind: MediaKind.Image } shown) return null;
        var usable = d.Assets.Where(a => !a.Missing && a.Kind == MediaKind.Image).ToList();
        return usable.FirstOrDefault(a => a.IsRaw && a.EditOf is null)
            ?? (shown.EditOf is { } source ? usable.FirstOrDefault(a => a.Id == source) : null)
            ?? usable.FirstOrDefault(a => a.Id == shown.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync()
    {
        if (EditTarget() is not { } target || _detail is not { } d) return;
        var entity = await Task.Run(() => Grid.Library.Items.GetAsset(target.Id));
        if (entity is not null) await _main.OpenEditorAsync(entity, d.Item.Rotation, d.Item.Squeeze);
    }

    [RelayCommand]
    private async Task ToggleInfoAsync()
    {
        ShowInfo = !ShowInfo;
        _main.ShowViewerInfo = ShowInfo;
        if (ShowInfo && Asset is { Missing: false } a) await LoadCameraAsync(a, _generation);
    }

    private void ShowSqueeze(ItemDto item)
    {
        _showingSqueeze = true;
        try
        {
            CanSqueeze = item.Kind is MediaKind.Image or MediaKind.Video;
            var factors = Squeeze.Common.Prepend(1.0).Append(item.Squeeze).Distinct().Order().ToList();
            SqueezeOptions = factors.Select(f => new SqueezeOption(f)).ToList();
            SelectedSqueeze = SqueezeOptions.First(o => o.Factor == item.Squeeze);
        }
        finally { _showingSqueeze = false; }
    }

    partial void OnSelectedSqueezeChanged(SqueezeOption? value)
    {
        if (_showingSqueeze || value is null || _detail is not { } d || value.Factor == d.Item.Squeeze) return;
        _ = SetSqueezeAsync(d.Item.Id, value.Factor);
    }

    /// <summary>Stretches the item by an anamorphic factor (1: none): its thumbnails, preview, edits and exports.</summary>
    private async Task SetSqueezeAsync(long itemId, double factor)
    {
        var lib = Grid.Library;
        await Task.Run(() => lib.Items.SetSqueeze([itemId], factor));
        await Grid.ReloadAsync(keepPosition: true);
        await ShowAsync(Index);
    }

    [RelayCommand]
    private async Task ShowVariationAsync(VariationRow row)
    {
        if (row.Asset.Missing || row.IsCurrent) return;
        await ShowAssetAsync(row.Asset, ++_generation);
    }

    /// <summary>Deletes a copy made in the photo editor (never an original), after asking, so it can be exported again.</summary>
    [RelayCommand]
    private async Task DeleteEditAsync(VariationRow row)
    {
        if (!row.CanDelete || _main.Shell is not { } shell) return;
        var choice = await shell.ChooseAsync("Delete the edited copy?",
            $"{row.FileName} was made in the photo editor; the original is not touched. Export again from the editor for a new copy, with the original's metadata.",
            ["Delete", "Cancel"]);
        if (choice != 0) return;
        var lib = Grid.Library;
        await Task.Run(() => lib.Exports.DeleteEdit(row.Asset.Id));
        await Grid.ReloadAsync(keepPosition: true);
        await ShowAsync((int)Math.Min(Index, Math.Max(0, Grid.Total - 1)));
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task OpenFileAsync() => Asset is { } a && _main.Shell is { } shell ? shell.OpenFileAsync(a.Path) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasFile))]
    private Task ShowInFolderAsync() => Asset is { } a && _main.Shell is { } shell ? shell.RevealAsync(a.Path) : Task.CompletedTask;

    public void Dispose()
    {
        _generation++;
        SetImage(null);
        ShowSphere(null);
    }
}

/// <summary>
/// How the viewer lays out its photo: fitted to the window, or zoomed to <paramref name="Width"/> × <paramref name="Height"/>
/// device-independent units, with each pixel drawn as a sharp square when <paramref name="Pixels"/>. <paramref name="Picture"/>
/// counts the pictures shown, so the view can centre a new one.
/// </summary>
public readonly record struct ZoomView(bool Zoomed, double Width, double Height, bool Pixels, int Picture);

/// <summary>A level in the viewer's zoom menu (0: fit, labelled with its own percentage).</summary>
public sealed partial class ZoomOption(double level) : ObservableObject
{
    public double Level { get; } = level;
    [ObservableProperty] private string _label = level == 0 ? "Fit" : $"{level * 100:0}%";
    public override string ToString() => Label;
}

/// <summary>A person in the photo, in the viewer's info panel.</summary>
public sealed record PersonChip(long Id, string Name);

/// <summary>A group of the file's metadata in the info panel (EXIF, a maker note, GPS, XMP…): its tags, maybe filtered, of <paramref name="Count"/>.</summary>
public sealed record MetadataGroupRow(string Name, int Count, IReadOnlyList<MetaTag> Tags)
{
    public string Heading => Tags.Count == Count ? $"{Name} · {Count}" : $"{Name} · {Tags.Count} of {Count}";
}

/// <summary>A choice in the info panel's 360° menu: null for as detected.</summary>
public sealed record ProjectionOption(bool? Sphere, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An anamorphic factor in the viewer's menu.</summary>
public sealed record SqueezeOption(double Factor)
{
    public override string ToString() => Factor == 1 ? "None" : $"{Squeeze.Format(Factor)}×";
}

/// <summary>A file of the item in the viewer's info panel.</summary>
public sealed record VariationRow(AssetDto Asset, bool IsPrimary, bool IsCurrent)
{
    public string FileName => Asset.FileName;
    public string Path => Asset.Path;
    public string Meta => string.Join(" · ", new[]
    {
        Asset.Width is { } w ? $"{w}×{Asset.Height}" : null, Format.Bytes(Asset.Size),
        Asset.IsRaw ? "RAW" : null, Asset.EditOf is not null ? "edited" : null, Asset.Identical ? "identical copy" : null,
        Asset.Missing ? "missing" : null, IsPrimary ? "primary" : null,
    }.Where(s => s is not null));
    public bool CanShow => !Asset.Missing && !IsCurrent;
    /// <summary>A copy made in the photo editor, which can be deleted (an original never is).</summary>
    public bool CanDelete => Asset.EditOf is not null;
}
