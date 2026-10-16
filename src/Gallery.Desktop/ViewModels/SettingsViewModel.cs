using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Faces;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The settings view. Every folder can be typed or chosen in the system dialog. The source folders and the folder for
/// transcodes and edits are saved in the library at once. The library folder and the ffmpeg paths take effect when the
/// library is opened again.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _dataDir = main.Settings.DataDir;
        _ffmpegPath = main.Settings.FfmpegPath;
        _ffprobePath = main.Settings.FfprobePath;
    }

    public ObservableCollection<SourceEntryViewModel> Sources { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LibraryChanged))] [NotifyCanExecuteChangedFor(nameof(ApplyLibraryCommand))]
    private string _dataDir;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LibraryChanged))] [NotifyCanExecuteChangedFor(nameof(ApplyLibraryCommand))]
    private string _ffmpegPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LibraryChanged))] [NotifyCanExecuteChangedFor(nameof(ApplyLibraryCommand))]
    private string _ffprobePath;

    [ObservableProperty] private string _derivedDir = "";
    [ObservableProperty] private string _derivedDefault = "";
    [ObservableProperty] private string _newSource = "";
    [ObservableProperty] private bool _hasNoSources;
    [ObservableProperty] private string? _sourceMessage;
    [ObservableProperty] private string? _libraryMessage;
    [ObservableProperty] private string? _derivedMessage;
    [ObservableProperty] private string? _actionMessage;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))] private bool _isScanning;

    /// <summary>The library folder or ffmpeg paths differ from those of the open library.</summary>
    public bool LibraryChanged =>
        !string.Equals(DataDir.Trim(), _main.Settings.DataDir, StringComparison.Ordinal)
        || FfmpegPath.Trim() != _main.Settings.FfmpegPath
        || FfprobePath.Trim() != _main.Settings.FfprobePath;

    private LibraryHost? Library => _main.Library;

    /// <summary>Reads what belongs to the open library: its source folders and its folder for transcodes and edits.</summary>
    public async Task LoadAsync()
    {
        DataDir = _main.Settings.DataDir;
        FfmpegPath = _main.Settings.FfmpegPath;
        FfprobePath = _main.Settings.FfprobePath;
        await LoadSourcesAsync();
        if (Library is not { } lib) { DerivedDir = ""; DerivedDefault = ""; return; }
        (DerivedDir, DerivedDefault) = await Task.Run(() => (lib.Settings.Get(SettingsService.DerivedDir), Path.Combine(lib.Options.ResolvedDataDir, "derived")));
        await LoadFacesAsync();
    }

    // ---------------------------------------------------------------- faces (as Admin → Faces in the web gallery)

    private bool _loadingFaces;

    public IReadOnlyList<string> FacePacks { get; } = FaceModels.DefaultPacks.Select(p => p.Name).ToList();
    [ObservableProperty] private bool _facesEnabled;
    [ObservableProperty] private string _facePack = FaceModels.DefaultPacks[0].Name;
    [ObservableProperty] private string _facePackInfo = "";
    [ObservableProperty] private string _faceStatus = "";
    [ObservableProperty] private string _faceDownloadText = "Download models";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DownloadFacesCommand))] private bool _canDownloadFaces;
    [ObservableProperty] private bool _faceModelsMissing;

    /// <summary>Reads the face settings, the pack's files and the counts (a library query: not on every status update).</summary>
    public async Task LoadFacesAsync()
    {
        if (Library is not { } lib) return;
        var (enabled, pack, counts) = await Task.Run(() => (lib.Faces.Enabled, lib.Faces.Pack, lib.FaceRepository.Counts(lib.Faces.Pack.Name)));
        _loadingFaces = true;
        try { FacesEnabled = enabled; FacePack = pack.Name; }
        finally { _loadingFaces = false; }
        _faceCounts = $"Photos: {counts.Photos:N0} · searched {counts.Done:N0} · to do {counts.Pending:N0}" + (counts.Failed > 0 ? $" · could not be read {counts.Failed:N0}" : "")
            + $"\nFaces: {counts.Faces:N0} in {counts.PhotosWithFaces:N0} photos";
        ShowFaceActivity(lib.Faces.Activity);
    }

    private string _faceCounts = "";

    /// <summary>Called with each status snapshot: what the face queue and the download are doing.</summary>
    public void ShowFaceActivity(FaceActivity? activity)
    {
        if (Library is not { } lib || activity is null) return;
        var pack = lib.FaceModels.Find(FacePack) ?? lib.Faces.Pack;
        var present = lib.FaceModels.IsPresent(pack);
        var download = lib.FaceModels.Download;
        var downloading = download is { Running: true };
        FaceModelsMissing = !present || downloading;
        CanDownloadFaces = !present && !downloading;
        static string Mb(long bytes) => $"{Math.Round(bytes / 1e6):N0} MB";
        FaceDownloadText = downloading ? $"Downloading {download!.Pack}… {Mb(download.Done)} of {Mb(download.Total)}" : $"Download {pack.Name} ({Mb(pack.ZipSize)})";
        FacePackInfo = present
            ? $"{pack.Description}. {pack.Detector.Name} and {pack.Recognizer.Name} are in {Path.Combine(lib.FaceModels.Dir, pack.Name)}."
            : $"{pack.Description}. Not downloaded yet: {pack.Detector.Name} and {pack.Recognizer.Name} from InsightFace's release, or copy them to {Path.Combine(lib.FaceModels.Dir, pack.Name)}.";
        var state = activity.State switch
        {
            "off" => "Off",
            "no-models" => "Waiting for the model files",
            "downloading" => "Downloading the model files",
            "error" => $"Stopped by an error: {activity.Detail}",
            _ => activity.Running ? (activity.Pending > 0 ? $"Finding faces: {activity.Done:N0} of {activity.Done + activity.Pending:N0} photos, {activity.Faces:N0} faces found" : "Checking which photos need searching…") : "Ready",
        };
        if (lib.Faces.Backends is { } backends && activity.State == "ready") state += $" · {backends}";
        if (download?.Error is { } error) state += $"\nDownload of {download.Pack} failed: {error}";
        FaceStatus = string.IsNullOrEmpty(_faceCounts) ? state : $"{state}\n{_faceCounts}";
    }

    partial void OnFacesEnabledChanged(bool value) => ConfigureFaces(value, null);
    partial void OnFacePackChanged(string value) => ConfigureFaces(null, value);

    private void ConfigureFaces(bool? enabled, string? pack)
    {
        if (_loadingFaces || Library is not { } lib) return;
        lib.Faces.Configure(enabled, pack);
        _ = LoadFacesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadFaces))]
    private void DownloadFaces()
    {
        if (Library is not { } lib || lib.FaceModels.Find(FacePack) is not { } pack) return;
        ActionMessage = lib.Faces.StartDownload(pack) ? $"Downloading the {pack.Name} face models." : "A download is already running.";
        ShowFaceActivity(lib.Faces.Activity);
    }

    [RelayCommand]
    private async Task RetryFacesAsync()
    {
        if (Library is not { } lib) return;
        var n = await Task.Run(() => lib.Faces.FindAgain(failedOnly: true));
        ActionMessage = n == 0 ? "No photos failed." : $"Trying {n:N0} photos again.";
        await LoadFacesAsync();
    }

    [RelayCommand]
    private async Task FindFacesAgainAsync()
    {
        if (Library is not { } lib) return;
        await Task.Run(() => lib.Faces.FindAgain(failedOnly: false));
        ActionMessage = "Finding faces again in every photo; faces found so far stay until each photo is done again.";
        await LoadFacesAsync();
    }

    public async Task LoadSourcesAsync()
    {
        Sources.Clear();
        if (Library is not { } lib) { HasNoSources = false; return; }
        foreach (var s in await Task.Run(lib.Sources.All)) Sources.Add(new SourceEntryViewModel(lib, s));
        HasNoSources = Sources.Count == 0;
    }

    private Task<string?> PickFolderAsync(string title, string? start) => _main.Shell?.PickFolderAsync(title, start) ?? Task.FromResult<string?>(null);
    private Task<string?> PickFileAsync(string title, string? start) => _main.Shell?.PickFileAsync(title, start) ?? Task.FromResult<string?>(null);

    [RelayCommand]
    private async Task BrowseDataDirAsync() { if (await PickFolderAsync("Choose the library folder", DataDir) is { } p) DataDir = p; }

    [RelayCommand]
    private async Task BrowseFfmpegAsync() { if (await PickFileAsync("Choose the ffmpeg program", FfmpegPath) is { } p) FfmpegPath = p; }

    [RelayCommand]
    private async Task BrowseFfprobeAsync() { if (await PickFileAsync("Choose the ffprobe program", FfprobePath) is { } p) FfprobePath = p; }

    [RelayCommand]
    private async Task BrowseDerivedAsync() { if (await PickFolderAsync("Choose the folder for transcodes and edits", string.IsNullOrWhiteSpace(DerivedDir) ? DerivedDefault : DerivedDir) is { } p) DerivedDir = p; }

    /// <summary>Chooses a folder in the dialog and adds it straight away.</summary>
    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (await PickFolderAsync("Add a folder of photos, videos or music", NewSource) is not { } p) return;
        NewSource = p;
        await AddSourceAsync();
    }

    /// <summary>Adds the typed folder and starts a scan so its files appear.</summary>
    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (Library is not { } lib) return;
        var path = NewSource;
        var error = await Task.Run(() => lib.Sources.Add(path));
        if (error is not null) { SourceMessage = error; return; }
        NewSource = "";
        await LoadSourcesAsync();
        lib.Scanner.RequestScan();
        SourceMessage = $"Added {GalleryOptions.ExpandHome(path.Trim())}; scanning it now.";
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(SourceEntryViewModel source)
    {
        if (Library is not { } lib) return;
        await Task.Run(() => lib.Sources.Remove(source.Id));
        await LoadSourcesAsync();
        SourceMessage = $"Removed {source.Path}. Its items leave the gallery at the next scan; the files are not touched.";
    }

    [RelayCommand]
    private async Task SaveDerivedAsync()
    {
        if (Library is not { } lib) return;
        var value = DerivedDir.Trim();
        try
        {
            if (value.Length > 0) Directory.CreateDirectory(GalleryOptions.ExpandHome(value));
            await Task.Run(() => lib.Settings.Set(SettingsService.DerivedDir, value));
            DerivedMessage = value.Length == 0 ? $"Using the default folder, {DerivedDefault}." : $"New transcodes and edits go to {GalleryOptions.ExpandHome(value)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DerivedMessage = $"Cannot use that folder: {ex.Message}";
        }
    }

    /// <summary>Saves the library folder and ffmpeg paths and reopens the library with them.</summary>
    [RelayCommand(CanExecute = nameof(LibraryChanged))]
    private async Task ApplyLibraryAsync()
    {
        var next = _main.Settings.Clone();
        next.DataDir = DataDir.Trim().Length > 0 ? DataDir.Trim() : new DesktopSettings().DataDir;
        next.FfmpegPath = FfmpegPath.Trim().Length > 0 ? FfmpegPath.Trim() : "ffmpeg";
        next.FfprobePath = FfprobePath.Trim().Length > 0 ? FfprobePath.Trim() : "ffprobe";
        LibraryMessage = $"Opening the library in {GalleryOptions.ExpandHome(next.DataDir)}…";
        await _main.ReopenLibraryAsync(next);
        LibraryMessage = _main.Library is null ? _main.StatusText : $"Library open: {_main.Library.Options.ResolvedDataDir}";
        OnPropertyChanged(nameof(LibraryChanged));
        ApplyLibraryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ScanNow()
    {
        if (Library is not { } lib) return;
        ActionMessage = lib.Scanner.RequestScan() ? "Scan started: deleted files are removed first, then new and changed files are indexed." : "A scan is already queued.";
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        Library?.Scanner.CancelScan();
        ActionMessage = "Scan cancelled.";
    }

    /// <summary>Repairs items without a main file and queues every missing thumbnail, as in the web admin page.</summary>
    [RelayCommand]
    private async Task GenerateThumbnailsAsync()
    {
        if (Library is not { } lib) return;
        var repaired = await Task.Run(lib.Scanner.RepairIncompleteItems);
        lib.Thumbnails.ForgetFailures();
        var queued = await lib.Thumbnails.SweepAsync(CancellationToken.None);
        ActionMessage = queued == 0 && repaired == 0 ? "No missing thumbnails." : $"{(repaired > 0 ? $"Repaired {repaired:N0} items · " : "")}{queued:N0} thumbnails queued.";
    }
}

/// <summary>A source folder in the settings list. Unchecking it stops indexing the folder but keeps it in the list.</summary>
public sealed partial class SourceEntryViewModel : ObservableObject
{
    private readonly LibraryHost _library;

    public SourceEntryViewModel(LibraryHost library, SourceInfo source)
    {
        _library = library;
        Id = source.Id;
        Path = source.Path;
        Exists = source.Exists;
        _enabled = source.Enabled;
    }

    public long Id { get; }
    public string Path { get; }
    public bool Exists { get; }
    public bool IsMissing => !Exists;

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value) => _library.Sources.SetEnabled(Id, value);
}
