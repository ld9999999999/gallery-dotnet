using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Services.Develop;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The editor's export panel: size, quality, colour detail, location, and where the JPEG goes (the library, as a
/// variation of the photo, and/or a copy saved to a chosen file). The choices are remembered for next time.
/// </summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private int _estimateRun;

    /// <summary>For a picture of <paramref name="width"/> × <paramref name="height"/> at full size: the photo's size, or its crop's when <paramref name="cropped"/>.</summary>
    internal ExportViewModel(EditorViewModel editor, ExportPreferences prefs, int width, int height, bool cropped = false)
    {
        _editor = editor;
        Sizes =
        [
            new($"{(cropped ? "Full size, cropped" : "Original")} ({width} × {height})", "original"),
            new("3840 px (long edge)", "3840"),
            new("2048 px", "2048"),
            new("1080 px", "1080"),
            new("Custom long edge…", "custom"),
            new("Percentage…", "percent"),
        ];
        _size = Sizes.FirstOrDefault(c => c.Value == prefs.Size) ?? Sizes[0];
        _sizeValue = prefs.SizeValue;
        _quality = Math.Clamp(prefs.Quality, 1, 100);
        _fullChroma = prefs.FullChroma;
        _keepLocation = prefs.KeepLocation;
        _saveToGallery = prefs.SaveToGallery;
        _saveCopy = prefs.SaveCopy;
    }

    public IReadOnlyList<Choice> Sizes { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSizeValue), nameof(SizeValueHint))] private Choice _size;
    [ObservableProperty] private decimal? _sizeValue;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QualityText))] private double _quality;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HalfChroma))] private bool _fullChroma;
    [ObservableProperty] private bool _keepLocation;
    [ObservableProperty] private bool _saveToGallery;
    [ObservableProperty] private bool _useAsPrimary;
    [ObservableProperty] private bool _saveCopy;
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool _isExporting;

    public bool ShowSizeValue => Size.Value is "custom" or "percent";
    public string SizeValueHint => Size.Value == "percent" ? "Percent, 1–100" : "Long edge in pixels";
    public string QualityText => $"{Quality:0}";
    public bool HalfChroma { get => !FullChroma; set => FullChroma = !value; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Size) or nameof(SizeValue) or nameof(Quality) or nameof(FullChroma)) _ = EstimateAsync();
    }

    /// <summary>The options in the form the export service takes.</summary>
    internal ExportOptions Options()
    {
        var value = (int)Math.Round(SizeValue ?? 0);
        return new ExportOptions
        {
            LongEdge = Size.Value switch { "original" or "percent" => null, "custom" => value > 0 ? value : null, var px => int.Parse(px) },
            Percent = Size.Value == "percent" ? Math.Clamp(value > 0 ? value : 50, 1, 100) : null,
            Quality = (int)Math.Round(Quality),
            FullChroma = FullChroma,
            KeepLocation = KeepLocation,
            SaveToGallery = SaveToGallery,
            UseAsPrimary = SaveToGallery && UseAsPrimary,
            Download = SaveCopy,
        };
    }

    /// <summary>About how large the JPEG will be. Worked out a moment after the options stop changing.</summary>
    internal async Task EstimateAsync()
    {
        var run = ++_estimateRun;
        Estimate = "Estimating the file size…";
        await Task.Delay(300);
        if (run != _estimateRun || !_editor.IsLoaded) return;
        try
        {
            var (w, h, bytes) = await _editor.EstimateAsync(Options());
            if (run == _estimateRun) Estimate = $"About {Format.Bytes(bytes)} · {w} × {h}";
        }
        catch (Exception ex) when (ex is DevelopException or IOException or InvalidDataException)
        {
            if (run == _estimateRun) Estimate = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!SaveToGallery && !SaveCopy) { State = "Choose the library, a copy in a file, or both."; return; }
        IsExporting = true;
        try
        {
            State = "";
            var error = await _editor.ExportAsync(this, Options(), SaveCopy);
            State = error ?? "";
            if (error is null) Remember();
        }
        finally { IsExporting = false; }
    }

    private bool CanRun() => !IsExporting;

    private void Remember()
    {
        var p = _editor.Main.Settings.Export;
        p.Size = Size.Value;
        p.SizeValue = (int)Math.Round(SizeValue ?? p.SizeValue);
        p.Quality = (int)Math.Round(Quality);
        p.FullChroma = FullChroma;
        p.KeepLocation = KeepLocation;
        p.SaveToGallery = SaveToGallery;
        p.SaveCopy = SaveCopy;
        _editor.Main.SaveSettings();
    }
}
