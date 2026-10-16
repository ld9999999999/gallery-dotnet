using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Develop;
using Gallery.Desktop.Services;
using Gallery.Develop;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The photo editor (docs/raw-editor.md, phase 7), the same as the web gallery's. It renders the library's editing copy
/// of the photo with Gallery.Develop. It offers slider groups, RGB curves, a histogram, Before, undo and redo of the last 4
/// changes, Reset, Save, crop and straighten, 100% and 200% views, recipes and JPEG export.
/// Only the newest frame request is rendered. While a slider or curve point is held, frames are half size; a full frame
/// follows when it is let go.
/// </summary>
public sealed partial class EditorViewModel : ViewModelBase, IDisposable
{
    public const int UndoSteps = 4;
    private static readonly string[] ChannelColours = ["#e6e8ee", "#ff6b6b", "#5fd068", "#5b9dff"];

    private readonly EditorRenderer _renderer = new();
    private readonly List<DevelopSettings> _undo = [], _redo = [];
    private readonly DispatcherTimer _commitTimer, _noticeTimer, _fitTimer;
    private readonly Dictionary<(int, int), FrameBuffers> _buffers = new();
    private readonly List<FrameBuffers> _retired = [];
    private DevelopSettings _settings = DevelopSettings.Default, _defaults = DevelopSettings.Default, _saved = DevelopSettings.Default;
    private DevelopSettings? _changeFrom; // the settings when the current change started
    private int _gestures;
    private (DevelopSettings Settings, bool Quick, bool Whole)? _wanted;
    private bool _rendering, _disposed, _lastQuick;
    private DevelopInfo? _info;
    private CropFrame? _cropFrame;
    private CropSettings? _shownCrop; // the crop the panel and the frame showed last
    private (CropBox Start, CropHandle Handle, double? Ratio)? _cropDrag;
    private CropBox? _straightenFrom; // the frame when the current straighten started; each new angle is fitted from it
    private bool _syncingCrop;
    private (double Width, double Height, double Scaling) _stage = (1200, 800, 1);
    private ZoomArea? _zoom;
    private readonly TaskCompletionSource _stageKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A zoomed view: the part of the cropped full-size picture it shows, the screen pixels per picture pixel, the
    /// centre point, and the crop it was cut for. The centre is kept exactly (the region is in whole pixels), so
    /// switching between 100% and 200% does not drift.
    /// </summary>
    private sealed class ZoomArea(int x, int y, int w, int h, int scale, double centreX, double centreY, CropSettings crop)
    {
        public CropSettings Crop { get; } = crop;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int W { get; } = w;
        public int H { get; } = h;
        public int Scale { get; } = scale;
        public double CentreX { get; } = centreX;
        public double CentreY { get; } = centreY;
    }

    public const int MaxZoom = 2;

    public EditorViewModel(MainViewModel main, LibraryHost library, Asset asset, int rotation, double squeeze = 1)
    {
        Main = main;
        Library = library;
        Asset = asset;
        Rotation = rotation;
        Squeeze = Core.Services.Squeeze.Normalize(squeeze);
        Title = asset.FileName;
        Groups = EditorSliders.Build(OnSlider);
        Straighten = EditorSliders.Straighten(OnStraighten);
        _cropRatio = Ratios[0];
        _commitTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => CommitChange());
        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => { _noticeTimer!.Stop(); Notice = null; });
        _fitTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, async (_, _) => { _fitTimer!.Stop(); await RefitAsync(); });
    }

    public MainViewModel Main { get; }
    public LibraryHost Library { get; }
    public Asset Asset { get; }
    public int Rotation { get; }
    /// <summary>How much an anamorphic lens stretches the photo's long side (1: no stretch).</summary>
    public double Squeeze { get; }
    public IReadOnlyList<SliderGroup> Groups { get; }
    /// <summary>The crop panel's slider that rotates the picture under the frame.</summary>
    public SliderViewModel Straighten { get; }
    public ObservableCollection<RecipeRow> Recipes { get; } = new();
    /// <summary>The settings on screen (ignoring Before). Tests read this.</summary>
    public DevelopSettings Settings => _settings;
    /// <summary>True if anything was saved or exported, so the grids and viewer refresh when the editor closes.</summary>
    public bool Changed { get; private set; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private Bitmap? _frame;
    /// <summary>The picture's size on screen (device-independent). A whole-picture frame fills it, half size or not.</summary>
    [ObservableProperty] private double _frameWidth;
    [ObservableProperty] private double _frameHeight;
    [ObservableProperty] private int _framesShown;
    [ObservableProperty] private string _timing = "";
    [ObservableProperty] private int[]? _histogram;
    [ObservableProperty] private string _clipText = "";
    [ObservableProperty] private bool _isComparing;
    /// <summary>0: the whole picture fitted to the window; 1: 100% (a picture pixel per screen pixel); 2: 200%.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsZoomed), nameof(Is100), nameof(Is200))] private int _zoomLevel;
    /// <summary>How the picture is scaled on screen: smooth, except at 200%, where each pixel is a sharp square.</summary>
    [ObservableProperty] private BitmapInterpolationMode _interpolation = BitmapInterpolationMode.HighQuality;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _hasSaved;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private bool _canUndo;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RedoCommand))] private bool _canRedo;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ResetCommand))] private bool _canReset;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _canSave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRgbCurve), nameof(IsRedCurve), nameof(IsGreenCurve), nameof(IsBlueCurve), nameof(CurveColour))]
    private int _curveChannel;
    [ObservableProperty] private double[][] _curvePoints = CurveSettings.Identity;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))] private RecipeRow? _selectedRecipe;
    [ObservableProperty] private bool _isRecipeFormOpen;
    [ObservableProperty] private string _recipeName = "";
    [ObservableProperty] private bool _keepWhiteBalance = true;
    [ObservableProperty] private bool _keepTone = true;
    [ObservableProperty] private bool _keepColour = true;
    [ObservableProperty] private bool _keepCurves = true;
    [ObservableProperty] private bool _keepDetail = true;
    [ObservableProperty] private string _recipeFormMessage = "";
    [ObservableProperty] private bool _isRecipeListOpen;
    [ObservableProperty] private bool _noRecipes = true;
    [ObservableProperty] private ExportViewModel? _export;
    [ObservableProperty] private bool _isExportOpen;

    /// <summary>Crop mode: the whole picture is shown rotated under the crop frame, which can be moved and resized.</summary>
    [ObservableProperty] private bool _isCropping;
    /// <summary>The frame on screen is the whole picture (crop mode).</summary>
    [ObservableProperty] private bool _showsWhole;
    /// <summary>The Straighten slider is moving, so the frame shows a fine grid to line things up with.</summary>
    [ObservableProperty] private bool _isStraightening;
    [ObservableProperty] private Choice _cropRatio;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TurnCropCommand))] private bool _canTurnCrop;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ResetCropCommand))] private bool _canResetCrop;

    public bool IsRgbCurve { get => CurveChannel == 0; set { if (value) CurveChannel = 0; } }
    public bool IsRedCurve { get => CurveChannel == 1; set { if (value) CurveChannel = 1; } }
    public bool IsGreenCurve { get => CurveChannel == 2; set { if (value) CurveChannel = 2; } }
    public bool IsBlueCurve { get => CurveChannel == 3; set { if (value) CurveChannel = 3; } }
    public string CurveColour => ChannelColours[CurveChannel];

    public bool IsZoomed => ZoomLevel > 0;
    public bool Is100 => ZoomLevel == 1;
    public bool Is200 => ZoomLevel == 2;

    public bool IsDirty => IsLoaded && !_settings.Equals(_saved);
    /// <summary>A frame is rendering or waiting to render (for tests).</summary>
    internal bool IsRendering => _rendering || _wanted is not null;

    // ---------- opening ----------

    /// <summary>Reads the photo's details and saved edit, makes or reuses its editing copy, and shows the first frame.</summary>
    public async Task LoadAsync()
    {
        var develop = Library.Develop;
        try
        {
            Message = "Opening the editing copy…";
            var info = await develop.GetInfoAsync(Asset, Rotation, Squeeze);
            if (_disposed) return;
            _info = info;
            _cropFrame = new CropFrame(info.Width, info.Height);
            var edit = await Task.Run(() => Library.Edits.Get(Asset.Id));
            _defaults = DevelopSettings.DefaultFor(info.SourceKind == DevelopFile.RawSource);
            _settings = edit?.Settings ?? _defaults;
            _saved = _settings;
            HasSaved = edit is not null;
            Subtitle = string.Join(" · ", new[]
            {
                info.Camera, $"{info.Width} × {info.Height}", info.Iso is { } iso ? $"ISO {iso}" : null, info.Shutter, info.Aperture,
            }.Where(s => !string.IsNullOrEmpty(s)));
            SyncControls();
            LoadRecipes();
            Message = info.SourceKind == DevelopFile.RawSource ? "Decoding the RAW file…" : "Preparing the photo…";
            // The window size decides how big the editing copy is, so give the view a moment to report it.
            try { await _stageKnown.Task.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { }
            var path = await develop.GetSourceAsync(Asset, CopySize(), Rotation, Squeeze);
            var source = await Task.Run(() => EditorRenderer.ReadEditingCopy(path));
            if (_disposed) return;
            _renderer.Load(source);
            await Task.Run(() => _renderer.Fit(StagePixels().W, StagePixels().H));
            if (_disposed) return;
            IsLoaded = true;
            IsLoading = false;
            Message = null;
            UpdateState();
            RequestRender();
        }
        catch (Exception ex) when (ex is DevelopException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_disposed) return;
            IsLoading = false;
            IsError = true;
            Message = ex.Message;
        }
    }

    /// <summary>The editing copy to use: big enough to fill the window in device pixels.</summary>
    private int CopySize()
    {
        var longest = Math.Max(_stage.Width, _stage.Height) * _stage.Scaling;
        return longest <= 1700 ? DevelopService.Sizes[0] : longest <= 2700 ? DevelopService.Sizes[1] : 3840;
    }

    private (int W, int H) StagePixels() =>
        (Math.Max(64, (int)Math.Round(_stage.Width * _stage.Scaling)), Math.Max(64, (int)Math.Round(_stage.Height * _stage.Scaling)));

    /// <summary>The area the picture can fill, from the view, in device-independent pixels.</summary>
    public void SetStage(double width, double height, double scaling)
    {
        if (width < 1 || height < 1) return;
        _stageKnown.TrySetResult();
        var next = (width, height, scaling);
        if (next == _stage) return;
        _stage = next;
        if (!IsLoaded) return;
        _fitTimer.Stop();
        _fitTimer.Start();
    }

    private async Task RefitAsync()
    {
        if (!IsLoaded || _disposed) return;
        var (w, h) = StagePixels();
        await Task.Run(() => _renderer.Fit(w, h));
        // A zoomed view cuts a new region the size of the new window, around the same point.
        if (_zoom is { } z) await ZoomToAsync(z.CentreX, z.CentreY, z.Scale);
        else RequestRender();
    }

    // ---------- rendering ----------

    /// <summary>Asks for a frame with the current settings: the whole picture in crop mode, else the cropped picture.</summary>
    private void RequestRender(bool quick = false)
    {
        if (!IsLoaded || _disposed) return;
        _wanted = (IsComparing ? _defaults : _settings, quick, IsCropping);
        _lastQuick = quick;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            while (_wanted is { } next && !_disposed)
            {
                _wanted = null;
                var frame = await Task.Run(() => _renderer.Render(next.Settings, next.Quick, next.Whole));
                if (_disposed) return;
                if (!frame.Detail && frame.Whole != IsCropping) continue; // crop mode changed meanwhile; wait for the next frame
                Show(frame);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            IsError = true;
            Message = ex.Message;
        }
        finally { _rendering = false; }
    }

    private void Show(EditorFrame f)
    {
        if (!_buffers.TryGetValue((f.Width, f.Height), out var buffers))
        {
            _buffers[(f.Width, f.Height)] = buffers = new FrameBuffers(f.Width, f.Height);
            RetireBuffers((f.Width, f.Height)); // new size (window, crop or view): drop buffers of unused sizes
        }
        var bitmap = buffers.Next(Frame);
        using (var fb = bitmap.Lock())
        {
            var row = f.Width * 4;
            if (fb.RowBytes == row) Marshal.Copy(f.Pixels, 0, fb.Address, row * f.Height);
            else for (var y = 0; y < f.Height; y++) Marshal.Copy(f.Pixels, y * row, fb.Address + y * fb.RowBytes, row);
        }
        Frame = bitmap;
        var s = _stage.Scaling;
        var (w, h) = f.Detail ? (f.Width * f.Scale, f.Height * f.Scale) : (f.ViewWidth, f.ViewHeight);
        ShowsWhole = f.Whole;
        FrameWidth = w / s;
        FrameHeight = h / s;
        Interpolation = f.Scale > 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        if (!f.Detail)
        {
            Histogram = f.Histogram;
            var pixels = Math.Max(1, f.Histogram[1024]);
            string Pct(int n) => (n * 100.0 / pixels).ToString(n * 100.0 / pixels < 1 ? "0.00" : "0.0", CultureInfo.CurrentCulture);
            ClipText = $"Clipped highlights {Pct(f.Histogram[1025])} % · shadows {Pct(f.Histogram[1026])} %";
        }
        Timing = $"{f.Width}×{f.Height}{(f.Detail ? $" at {f.Scale * 100}%" : "")} in {f.Milliseconds:0} ms";
        FramesShown++;
        foreach (var r in _retired.Where(r => !r.Holds(Frame)).ToList()) { r.Dispose(); _retired.Remove(r); }
    }

    /// <summary>Drops frame buffers for sizes not rendered any more (keeps <paramref name="shown"/>'s), e.g. after a resize, a new crop or leaving the 100% view.</summary>
    private void RetireBuffers((int, int)? shown = null)
    {
        var keep = new HashSet<(int, int)>(_renderer.FrameSizes);
        if (shown is { } size) keep.Add(size);
        foreach (var key in _buffers.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _retired.Add(_buffers[key]);
            _buffers.Remove(key);
        }
    }

    /// <summary>Two bitmaps of the same size: draw each frame into the one not on screen, then show it.</summary>
    private sealed class FrameBuffers(int width, int height) : IDisposable
    {
        private readonly WriteableBitmap _a = Make(width, height), _b = Make(width, height);

        private static WriteableBitmap Make(int w, int h) => new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);

        public WriteableBitmap Next(Bitmap? shown) => ReferenceEquals(shown, _a) ? _b : _a;
        public bool Holds(Bitmap? b) => ReferenceEquals(b, _a) || ReferenceEquals(b, _b);

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }
    }

    partial void OnIsComparingChanged(bool value) => RequestRender();

    // ---------- changes and undo ----------

    /// <summary>A slider or curve point is held: frames are half size, and the whole change is one undo step when let go.</summary>
    public void BeginGesture()
    {
        _gestures++;
        _commitTimer.Stop();
    }

    public void EndGesture()
    {
        if (_gestures == 0) return;
        if (--_gestures > 0) return;
        CommitChange();
        if (_lastQuick) RequestRender(); // a full frame after the half-size ones
    }

    private void OnSlider(SliderViewModel slider, double value) => Change(slider.Apply(_settings, value));

    /// <summary>The selected channel's curve, edited in the curve control.</summary>
    public void SetCurve(double[][] points)
    {
        if (!IsLoaded) return;
        CurvePoints = points;
        var c = _settings.Curves;
        Change(_settings with
        {
            Curves = CurveChannel switch { 0 => c with { Rgb = points }, 1 => c with { Red = points }, 2 => c with { Green = points }, _ => c with { Blue = points } },
        });
    }

    private void Change(DevelopSettings next)
    {
        if (!IsLoaded) return;
        _changeFrom ??= _settings;
        var shapeOnly = ShapeOnly(_settings, next);
        _settings = next;
        if (_gestures == 0)
        {
            // Keyboard changes made close together count as one undo step.
            _commitTimer.Stop();
            _commitTimer.Start();
        }
        if (!shapeOnly) RequestRender(quick: _gestures > 0);
        UpdateState();
    }

    /// <summary>True if only the crop changed in crop mode. The whole picture is shown then, so no new frame is needed.</summary>
    private bool ShapeOnly(DevelopSettings a, DevelopSettings b) => IsCropping && (a with { Crop = b.Crop }).Equals(b);

    private void CommitChange()
    {
        _commitTimer.Stop();
        _straightenFrom = null;
        IsStraightening = false;
        if (_changeFrom is not { } from) return;
        _changeFrom = null;
        Commit(from);
    }

    private void Commit(DevelopSettings previous)
    {
        if (!previous.Equals(_settings))
        {
            _undo.Add(previous);
            if (_undo.Count > UndoSteps) _undo.RemoveAt(0);
            _redo.Clear();
        }
        UpdateState();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        CommitChange();
        if (_undo.Count == 0) return;
        var previous = _settings;
        _redo.Add(_settings);
        _settings = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        AfterJump(previous);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        CommitChange();
        if (_redo.Count == 0) return;
        var previous = _settings;
        _undo.Add(_settings);
        _settings = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        AfterJump(previous);
    }

    /// <summary>Goes back to the photo's starting settings (for a RAW: some noise reduction and sharpening). Can be undone.</summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => Replace(_defaults);

    /// <summary>Replaces the settings as one undo step.</summary>
    private void Replace(DevelopSettings next)
    {
        CommitChange();
        var before = _settings;
        _settings = next;
        Commit(before);
        AfterJump(before);
    }

    /// <summary>Updates the controls and the picture after the settings jumped from <paramref name="previous"/>.</summary>
    private void AfterJump(DevelopSettings previous)
    {
        SyncControls();
        // A zoomed view is cut from the cropped picture, so an undone or redone crop needs a new region.
        if (_zoom is { } z && !SameShape(z.Crop, _settings.Crop)) _ = ZoomToAsync(z.CentreX, z.CentreY, z.Scale);
        else if (!ShapeOnly(previous, _settings)) RequestRender();
        UpdateState();
    }

    private void SyncControls()
    {
        foreach (var g in Groups)
            foreach (var s in g.Sliders) s.Sync(_settings);
        Straighten.Sync(_settings);
        CurvePoints = CurveOf(_settings, CurveChannel);
    }

    partial void OnCurveChannelChanged(int value) => CurvePoints = CurveOf(_settings, value);

    private static double[][] CurveOf(DevelopSettings s, int channel) => channel switch
    {
        0 => s.Curves.Rgb, 1 => s.Curves.Red, 2 => s.Curves.Green, _ => s.Curves.Blue,
    };

    private void UpdateState()
    {
        CanUndo = IsLoaded && (_undo.Count > 0 || (_changeFrom is { } f && !f.Equals(_settings)));
        CanRedo = IsLoaded && _redo.Count > 0;
        CanReset = IsLoaded && !_settings.Equals(_defaults);
        CanSave = IsDirty;
        StateText = IsDirty ? "Not saved" : HasSaved ? "Saved" : "";
        UpdateCrop();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        CommitChange();
        var settings = _settings;
        var record = await Task.Run(() => Library.Edits.Save(Asset.Id, settings));
        _saved = record.Settings;
        if (ReferenceEquals(_settings, settings)) _settings = record.Settings;
        HasSaved = true;
        Changed = true;
        Notify("Edit saved");
        UpdateState();
    }

    /// <summary>Leaves the editor, offering to save first when there are unsaved changes.</summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        CommitChange();
        if (IsDirty && Main.Shell is { } shell)
        {
            var choice = await shell.ChooseAsync("Unsaved changes", "Save the changes to this photo before closing the editor?", ["Save", "Don't save", "Cancel"]);
            if (choice == 0) await SaveAsync();
            else if (choice != 1) return;
        }
        Main.CloseEditor();
    }

    /// <summary>Escape: closes an open panel, else leaves crop mode, else leaves the editor.</summary>
    public Task EscapeAsync()
    {
        if (CloseDialogs()) return Task.CompletedTask;
        if (!IsCropping) return CloseAsync();
        ExitCrop();
        return Task.CompletedTask;
    }

    private bool CloseDialogs()
    {
        var open = IsExportOpen || IsRecipeFormOpen || IsRecipeListOpen;
        IsExportOpen = IsRecipeFormOpen = IsRecipeListOpen = false;
        return open;
    }

    private void Notify(string text)
    {
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ---------- 100% and 200% views ----------

    /// <summary>Z: shows the middle of the picture at 100%, or the whole picture again.</summary>
    [RelayCommand]
    private Task ToggleZoomAsync() => IsZoomed ? UnzoomAsync() : SetZoomAsync(1);

    /// <summary>The 100% button: shows the 100% view, or the whole picture if it is already showing.</summary>
    [RelayCommand]
    private Task Zoom100Async() => Is100 ? UnzoomAsync() : SetZoomAsync(1);

    /// <summary>The 200% button: shows the 200% view, or the whole picture if it is already showing.</summary>
    [RelayCommand]
    private Task Zoom200Async() => Is200 ? UnzoomAsync() : SetZoomAsync(2);

    /// <summary>⌘+ (Ctrl++): whole picture → 100% → 200%.</summary>
    [RelayCommand]
    private Task ZoomInAsync() => ZoomLevel < MaxZoom ? SetZoomAsync(ZoomLevel + 1) : Task.CompletedTask;

    /// <summary>⌘− (Ctrl+−): 200% → 100% → whole picture.</summary>
    [RelayCommand]
    private Task ZoomOutAsync() => ZoomLevel > 1 ? SetZoomAsync(ZoomLevel - 1) : IsZoomed ? UnzoomAsync() : Task.CompletedTask;

    /// <summary>⌘0 (Ctrl+0): the whole picture.</summary>
    [RelayCommand]
    private Task FitAsync() => UnzoomAsync();

    /// <summary>Zooms to 100% (1) or 200% (2), keeping the middle of what is on screen.</summary>
    public Task SetZoomAsync(int scale)
    {
        if (_zoom is { } z) return ZoomToAsync(z.CentreX, z.CentreY, scale);
        var (w, h) = CroppedSize();
        return _info is null ? Task.CompletedTask : ZoomToAsync(w / 2.0, h / 2.0, scale);
    }

    /// <summary>The crop without the ratio lock, which does not change what is drawn.</summary>
    private CropSettings Shape() => _settings.Crop.Clamped() with { Aspect = null };

    private static bool SameShape(CropSettings a, CropSettings b) => (a.Clamped() with { Aspect = null }).Equals(b.Clamped() with { Aspect = null });

    /// <summary>The size of the cropped and straightened picture in full-size pixels (used by the 100% view and export).</summary>
    private (int W, int H) CroppedSize() => _info is { } i ? Geometry.Size(Shape(), i.Width, i.Height) : (0, 0);

    /// <summary>
    /// Shows full-resolution pixels around (cx, cy), in full-size coordinates of the cropped picture, at
    /// <paramref name="scale"/> screen pixels per picture pixel (0 keeps the current zoom, or 100% from the whole picture).
    /// The region is only what fills the window, so half as wide and high at 200%. Leaves crop mode first.
    /// </summary>
    public async Task ZoomToAsync(double cx, double cy, int scale = 0)
    {
        if (!IsLoaded || _info is null) return;
        ExitCrop();
        scale = scale > 0 ? Math.Min(scale, MaxZoom) : _zoom?.Scale ?? 1;
        var crop = Shape();
        var (pw, ph) = CroppedSize();
        var (sw, sh) = StagePixels();
        int w = Math.Min(pw, Math.Min(4096, (int)Math.Ceiling(sw / (double)scale)));
        int h = Math.Min(ph, Math.Min(4096, (int)Math.Ceiling(sh / (double)scale)));
        // At an edge the region stops, and its middle becomes the new centre.
        cx = Math.Clamp(cx, w / 2.0, pw - w / 2.0);
        cy = Math.Clamp(cy, h / 2.0, ph - h / 2.0);
        int x = (int)Math.Round(cx - w / 2.0), y = (int)Math.Round(cy - h / 2.0);
        var zoom = _zoom = new ZoomArea(x, y, w, h, scale, cx, cy, crop);
        ZoomLevel = scale;
        if (!_renderer.HasDetail) Message = $"Loading the {scale * 100}% view…";
        try
        {
            var gdev = await Library.Develop.GetCropAsync(Asset, Rotation, x, y, w, h, Squeeze, crop);
            var detail = await Task.Run(() => LinearImage.FromGdev(gdev));
            if (_zoom != zoom || _disposed) return;
            _renderer.LoadDetail(detail, scale);
            Message = null;
            RequestRender();
        }
        catch (Exception ex) when (ex is DevelopException or IOException or InvalidDataException)
        {
            if (_zoom != zoom) return;
            Message = ex.Message;
            IsError = true;
        }
    }

    public Task UnzoomAsync()
    {
        ClearZoom();
        RequestRender();
        return Task.CompletedTask;
    }

    private void ClearZoom()
    {
        _zoom = null;
        ZoomLevel = 0;
        _renderer.ClearDetail();
        Message = null;
        RetireBuffers();
    }

    /// <summary>Moves the zoomed view by a drag of (dx, dy) device pixels (half as many picture pixels at 200%).</summary>
    public Task PanByAsync(double dx, double dy) =>
        _zoom is { } z ? ZoomToAsync(z.CentreX - dx / z.Scale, z.CentreY - dy / z.Scale, z.Scale) : Task.CompletedTask;

    /// <summary>Turns a point given as fractions of the picture's width and height into full-size coordinates in the cropped picture.</summary>
    public (double X, double Y)? PointOf(double fx, double fy)
    {
        if (_zoom is { } z) return (z.X + fx * z.W, z.Y + fy * z.H);
        if (_info is null) return null;
        var (w, h) = CroppedSize();
        return (fx * w, fy * h);
    }

    // ---------- crop and straighten ----------
    // Works like the web editor (wwwroot/editor.js). The crop is a frame level with the screen, cut from the picture
    // rotated about its centre by the angle. Gallery.Develop's Geometry does the cutting for frames, the 100% view and
    // export. In crop mode the whole picture is shown rotated under the frame. The frame can be moved and resized but
    // never leaves the picture; CropFrame has the maths, in picture pixels. Each drag, menu choice or button press is one
    // undo step.

    /// <summary>The ratio menu: the shapes the frame can be locked to.</summary>
    public IReadOnlyList<Choice> Ratios { get; } =
    [
        new("Free", "free"), new("Original", "original"), new("Square", "1:1"), new("3:2", "3:2"), new("4:3", "4:3"),
        new("5:4", "5:4"), new("7:5", "7:5"), new("16:9", "16:9"),
    ];

    /// <summary>The crop in the current settings.</summary>
    public CropSettings Crop => _settings.Crop;

    /// <summary>The picture size the crop frame is measured in (null until the photo's details are read).</summary>
    public CropFrame? CropFrame => _cropFrame;

    /// <summary>C or the Crop button: turns crop mode on or off.</summary>
    [RelayCommand]
    private void ToggleCrop()
    {
        if (IsCropping) ExitCrop();
        else EnterCrop();
    }

    public void EnterCrop()
    {
        if (!IsLoaded || IsCropping || _cropFrame is null) return;
        CommitChange();
        if (_zoom is not null) ClearZoom();
        IsCropping = true;
        RequestRender();
    }

    /// <summary>Done, Enter or Esc: shows the cropped picture again.</summary>
    [RelayCommand]
    public void ExitCrop()
    {
        if (!IsCropping) return;
        EndCropDrag();
        CommitChange();
        IsCropping = false;
        RequestRender();
    }

    /// <summary>The frame or one of its handles is pressed. The drag that follows is one undo step.</summary>
    public void BeginCropDrag(CropHandle handle)
    {
        if (!IsCropping || _cropFrame is not { } f || _cropDrag is not null) return;
        CommitChange();
        BeginGesture();
        var (start, angle) = (f.Of(_settings.Crop), _settings.Crop.Angle);
        // After rounding, a frame against an edge can be a tiny bit outside the picture. Then nothing counts as inside
        // and the drag cannot move it, so start from just inside.
        if (!f.Inside(start, angle)) start = f.Fit(start, angle);
        _cropDrag = (start, handle, f.LockedRatio(_settings.Crop));
    }

    /// <summary>Applies the drag so far, in picture pixels: moves the frame or its handle's edges, stopping at the picture's edges.</summary>
    public void DragCrop(double dx, double dy)
    {
        if (_cropDrag is not { } d || _cropFrame is not { } f) return;
        var angle = _settings.Crop.Angle;
        var box = d.Handle == CropHandle.Move ? f.Moved(d.Start, dx, dy, angle) : f.Resized(d.Start, d.Handle, dx, dy, angle, d.Ratio);
        Change(_settings with { Crop = f.With(_settings.Crop, box) });
    }

    public void EndCropDrag()
    {
        if (_cropDrag is null) return;
        _cropDrag = null;
        EndGesture();
    }

    /// <summary>
    /// Straighten: the picture rotates under the frame. The frame shrinks about its middle only as much as needed to stay
    /// inside. During one slider drag (or keys pressed close together) it is fitted from the starting frame, so rotating
    /// back makes it grow again.
    /// </summary>
    private void OnStraighten(SliderViewModel slider, double value)
    {
        if (!IsLoaded || _cropFrame is not { } f) return;
        var from = _straightenFrom ??= f.Of(_settings.Crop);
        var angle = slider.Rounded(value);
        IsStraightening = true;
        Change(_settings with { Crop = f.With(_settings.Crop with { Angle = angle }, f.Fit(from, angle)) });
    }

    /// <summary>Double-click on Straighten: sets the angle back to level, as one undo step.</summary>
    public void Level()
    {
        if (!IsLoaded || _settings.Crop.Angle == 0) return;
        CommitChange();
        OnStraighten(Straighten, 0);
        CommitChange();
        Straighten.Sync(_settings);
    }

    partial void OnCropRatioChanged(Choice value)
    {
        if (!_syncingCrop && value is not null) SetAspect(value.Value);
    }

    /// <summary>A ratio from the menu: the largest frame of that shape that fits in the current one, about its middle.</summary>
    private void SetAspect(string aspect)
    {
        if (!IsLoaded || _cropFrame is not { } f || aspect == (_settings.Crop.Aspect ?? "free")) return;
        var c = _settings.Crop with { Aspect = aspect };
        if (f.LockedRatio(c) is { } r)
        {
            var b = f.Of(c);
            double w = b.W, h = b.H;
            if (w / h > r) w = h * r; else h = w / r;
            c = f.With(c, f.Fit(new CropBox(b.X + (b.W - w) / 2, b.Y + (b.H - h) / 2, w, h), c.Angle));
        }
        Replace(_settings with { Crop = c });
    }

    /// <summary>Portrait ↔ landscape: swaps the frame's sides about its middle, and shrinks it if it does not fit.</summary>
    [RelayCommand(CanExecute = nameof(CanTurnCrop))]
    private void TurnCrop()
    {
        if (_cropFrame is not { } f) return;
        var c = _settings.Crop;
        var b = f.Of(c);
        Replace(_settings with { Crop = f.With(c, f.Fit(new CropBox(b.X + (b.W - b.H) / 2, b.Y + (b.H - b.W) / 2, b.H, b.W), c.Angle)) });
    }

    /// <summary>Resets to the whole picture, level. A locked ratio keeps the largest frame of that shape.</summary>
    [RelayCommand(CanExecute = nameof(CanResetCrop))]
    private void ResetCrop()
    {
        if (_cropFrame is not { } f) return;
        var c = new CropSettings { Aspect = _settings.Crop.Aspect };
        if (f.LockedRatio(c) is { } r && Math.Abs(r - f.Width / f.Height) > 1e-6)
        {
            double w = f.Width, h = f.Height;
            if (w / h > r) w = h * r; else h = w / r;
            c = f.With(c, new CropBox((f.Width - w) / 2, (f.Height - h) / 2, w, h));
        }
        Replace(_settings with { Crop = c });
    }

    /// <summary>Updates the crop panel from the settings, and tells the frame when the crop changed.</summary>
    private void UpdateCrop()
    {
        var c = _settings.Crop;
        CanResetCrop = IsLoaded && !c.IsIdentity;
        CanTurnCrop = IsLoaded && c.Aspect != "1:1";
        if (c.Equals(_shownCrop)) return;
        _shownCrop = c;
        _syncingCrop = true;
        try { CropRatio = Ratios.FirstOrDefault(r => r.Value == c.Aspect) ?? Ratios[0]; }
        finally { _syncingCrop = false; }
        OnPropertyChanged(nameof(Crop));
    }

    // ---------- recipes ----------

    private void LoadRecipes()
    {
        var keep = SelectedRecipe?.Id;
        Recipes.Clear();
        foreach (var r in Library.Recipes.All()) Recipes.Add(new RecipeRow(this, r));
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == keep);
        NoRecipes = Recipes.Count == 0;
    }

    private bool CanApplyRecipe() => SelectedRecipe is not null && IsLoaded;

    /// <summary>Replaces the matching sections of the current settings with the recipe's, as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRecipe))]
    private void ApplyRecipe()
    {
        if (SelectedRecipe?.Recipe is not { } r) return;
        Replace(_settings.With(r.Settings, r.Sections));
        Notify($"Applied \"{r.Name}\"");
    }

    [RelayCommand]
    private void OpenRecipeForm()
    {
        CloseDialogs();
        RecipeName = "";
        KeepWhiteBalance = KeepTone = KeepColour = KeepCurves = KeepDetail = true;
        RecipeFormMessage = "";
        IsRecipeFormOpen = true;
    }

    [RelayCommand]
    private void SaveRecipe()
    {
        var sections = (KeepWhiteBalance ? DevelopSections.WhiteBalance : 0) | (KeepTone ? DevelopSections.Tone : 0)
            | (KeepColour ? DevelopSections.Color : 0) | (KeepCurves ? DevelopSections.Curves : 0) | (KeepDetail ? DevelopSections.Detail : 0);
        CommitChange();
        try
        {
            var recipe = Library.Recipes.Create(RecipeName, _settings, sections);
            IsRecipeFormOpen = false;
            LoadRecipes();
            SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == recipe.Id);
            Notify($"Recipe \"{recipe.Name}\" saved");
        }
        catch (ArgumentException ex) { RecipeFormMessage = ex.Message; }
    }

    [RelayCommand]
    private void OpenRecipeList()
    {
        CloseDialogs();
        LoadRecipes();
        IsRecipeListOpen = true;
    }

    [RelayCommand]
    private void CloseDialog() => CloseDialogs();

    internal void RenameRecipe(RecipeRow row)
    {
        try
        {
            Library.Recipes.Rename(row.Id, row.Name);
            LoadRecipes();
        }
        catch (ArgumentException ex) { row.Message = ex.Message; }
    }

    internal async Task DeleteRecipeAsync(RecipeRow row)
    {
        if (Main.Shell is { } shell &&
            await shell.ChooseAsync("Delete recipe", $"Delete the recipe \"{row.Recipe.Name}\"? Photos it was applied to keep their edits.", ["Delete", "Cancel"]) != 0) return;
        Library.Recipes.Delete(row.Id);
        LoadRecipes();
    }

    // ---------- export ----------

    [RelayCommand]
    private void OpenExport()
    {
        if (!IsLoaded || _info is null) return;
        var open = IsExportOpen;
        CloseDialogs();
        if (open) return;
        var (w, h) = CroppedSize(); // export sizes are based on the cropped picture
        Export = new ExportViewModel(this, Main.Settings.Export, w, h, cropped: !_settings.Crop.IsIdentity);
        IsExportOpen = true;
        _ = Export.EstimateAsync();
    }

    internal Task<(int Width, int Height, long Bytes)> EstimateAsync(ExportOptions options)
    {
        var settings = _settings;
        return Task.Run(() => Library.Exports.EstimateAsync(Asset, settings, options));
    }

    /// <summary>Saves the current settings, then exports a JPEG. Returns an error message, or null on success.</summary>
    internal async Task<string?> ExportAsync(ExportViewModel panel, ExportOptions options, bool saveCopy)
    {
        string? copyTo = null;
        if (saveCopy)
        {
            if (Main.Shell is not { } shell) return "Saving a copy needs a window";
            copyTo = await shell.PickSaveFileAsync("Save the JPEG", $"{Path.GetFileNameWithoutExtension(Asset.FileName)}-edit.jpg", Main.Settings.Export.CopyFolder);
            if (copyTo is null) return null;
        }
        try
        {
            if (IsDirty) { panel.State = "Saving the edit…"; await SaveAsync(); }
            panel.State = "Exporting…";
            var job = Library.Exports.Queue(Asset.Id, _settings, options);
            while (job.State is "queued" or "running") await Task.Delay(150);
            if (job.State != "done" || job.FilePath is not { } file) return $"Not exported: {job.Message ?? "the export failed"}";
            if (copyTo is not null)
            {
                await Task.Run(() =>
                {
                    File.Copy(file, copyTo, overwrite: true);
                    if (!options.SaveToGallery) File.Delete(file);
                });
                Main.Settings.Export.CopyFolder = Path.GetDirectoryName(copyTo);
            }
            if (options.SaveToGallery) Changed = true;
            IsExportOpen = false;
            var size = $"{job.Width} × {job.Height}, {Format.Bytes(job.Bytes)}";
            Notify(options.SaveToGallery ? $"Exported {size}; added to the photo's files" : $"Exported {size}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Not exported: {ex.Message}"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commitTimer.Stop();
        _noticeTimer.Stop();
        _fitTimer.Stop();
        var buffers = _buffers.Values.Concat(_retired).ToList();
        _buffers.Clear();
        _retired.Clear();
        Frame = null;
        // Clear the picture on screen before freeing its bitmaps.
        Dispatcher.UIThread.Post(() => { foreach (var b in buffers) b.Dispose(); }, DispatcherPriority.Background);
    }
}

/// <summary>A recipe in the editor's list. It can be renamed in place or deleted.</summary>
public sealed partial class RecipeRow : ObservableObject
{
    private static readonly Dictionary<string, string> PartNames = new()
    {
        ["whiteBalance"] = "white balance", ["tone"] = "tone", ["color"] = "colour", ["curves"] = "curves", ["detail"] = "detail",
    };

    private readonly EditorViewModel _editor;

    internal RecipeRow(EditorViewModel editor, Recipe recipe)
    {
        _editor = editor;
        Recipe = recipe;
        _name = recipe.Name;
    }

    public Recipe Recipe { get; }
    public long Id => Recipe.Id;
    public string Parts => string.Join(", ", RecipeService.SectionList(Recipe.Sections).Select(s => PartNames[s]));

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRenamed))] [NotifyCanExecuteChangedFor(nameof(RenameCommand))] private string _name;
    [ObservableProperty] private string _message = "";

    public bool IsRenamed => Name.Trim().Length > 0 && Name.Trim() != Recipe.Name;

    [RelayCommand(CanExecute = nameof(IsRenamed))]
    private void Rename() => _editor.RenameRecipe(this);

    [RelayCommand]
    private Task DeleteAsync() => _editor.DeleteRecipeAsync(this);
}
