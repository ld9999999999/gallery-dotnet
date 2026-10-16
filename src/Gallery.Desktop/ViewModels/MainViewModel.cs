using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Gallery.Core.Services;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.ViewModels;

/// <summary>
/// The main window for one open library: the "All photos", "Albums", "Years" and "People" tabs, the settings view, the viewer,
/// the photo editor on top of the viewer, and a status bar with the library's totals and the scanner and thumbnail queue state.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    public const int AllPhotosTab = 0, AlbumsTab = 1, YearsTab = 2, PeopleTab = 3, SettingsTab = 4;

    private readonly string _settingsPath;
    private readonly DispatcherTimer _timer;
    private bool _wasScanning;
    private long _lastItems = -1;
    private DateTime _lastLiveRefresh;
    private bool _polling;
    private bool _wasGrouping;

    public MainViewModel(DesktopSettings settings, string settingsPath)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        _thumbSize = settings.ThumbSize;
        SettingsPage = new SettingsViewModel(this);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) => await PollAsync());
    }

    public DesktopSettings Settings { get; private set; }
    public LibraryHost? Library { get; private set; }
    /// <summary>Dialogs and the default apps, from the main window.</summary>
    public IDesktopShell? Shell { get; set; }
    public SettingsViewModel SettingsPage { get; }
    /// <summary>Whether the viewer opens with its info panel; kept while the app runs.</summary>
    public bool ShowViewerInfo { get; set; }
    /// <summary>Whether the info panel's "All metadata" section is open; it stays the same from one photo to the next.</summary>
    public bool ShowAllMetadata { get; set; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private AllPhotosViewModel? _allPhotos;
    [ObservableProperty] private AlbumsViewModel? _albums;
    [ObservableProperty] private YearsViewModel? _years;
    [ObservableProperty] private PeopleViewModel? _people;
    [ObservableProperty] private ViewerViewModel? _viewer;
    [ObservableProperty] private EditorViewModel? _editor;
    [ObservableProperty] private string _statusText = "Opening the library…";
    [ObservableProperty] private string _libraryText = "";
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private double _thumbSize;

    private IEnumerable<PhotoGridViewModel> Grids => new[] { AllPhotos?.Grid, Albums?.Grid, Years?.Grid, People?.Grid }.OfType<PhotoGridViewModel>();

    partial void OnThumbSizeChanged(double value)
    {
        foreach (var g in Grids) g.SetThumbSize(value);
        Settings.ThumbSize = value;
        SaveSettings();
    }

    /// <summary>Opens the library from the settings and starts watching its status.</summary>
    public async Task StartAsync()
    {
        await OpenLibraryAsync(startServices: true);
        _timer.Start();
        await PollAsync();
    }

    /// <summary>For tests: shows an already open library without starting the status timer.</summary>
    internal async Task AttachAsync(LibraryHost library)
    {
        Library = library;
        await ShowLibraryAsync();
        await PollAsync();
    }

    private async Task OpenLibraryAsync(bool startServices)
    {
        IsBusy = true;
        StatusText = $"Opening the library in {Settings.DataDir}…";
        try
        {
            Library = await LibraryHost.OpenAsync(Settings.ToOptions(), startServices);
        }
        catch (Exception ex)
        {
            Library = null;
            IsBusy = false;
            StatusText = $"Could not open the library in {Settings.DataDir}: {ex.Message}";
            await SettingsPage.LoadAsync();
            SettingsPage.LibraryMessage = StatusText;
            SelectedTab = SettingsTab;
            return;
        }
        await ShowLibraryAsync();
    }

    private async Task ShowLibraryAsync()
    {
        var lib = Library!;
        AllPhotos = new AllPhotosViewModel(lib);
        Albums = new AlbumsViewModel(lib);
        Years = new YearsViewModel(lib);
        People = new PeopleViewModel(lib, () => Shell);
        foreach (var g in Grids)
        {
            g.SetThumbSize(ThumbSize);
            g.OpenRequested = (grid, index) => _ = OpenViewerAsync(grid, index);
        }
        await SettingsPage.LoadAsync();
        await Task.WhenAll(AllPhotos.LoadAsync(), Albums.RefreshAsync(), Years.RefreshAsync(), People.RefreshAsync());
        if (SettingsPage.HasNoSources)
        {
            SettingsPage.SourceMessage = "Add a folder of photos, videos or music to get started.";
            SelectedTab = SettingsTab;
        }
    }

    /// <summary>Closes the open library and opens the one in next.DataDir (the settings view's "Apply").</summary>
    public async Task ReopenLibraryAsync(DesktopSettings next)
    {
        CloseEditor();
        CloseViewer();
        var old = Library;
        Library = null;
        AllPhotos = null;
        Albums = null;
        Years = null;
        People = null;
        if (old is not null) await old.DisposeAsync();
        Settings = next;
        SaveSettings();
        _wasScanning = false;
        _lastItems = -1;
        await OpenLibraryAsync(startServices: true);
        await PollAsync();
    }

    public async Task OpenViewerAsync(PhotoGridViewModel grid, int index)
    {
        Viewer?.Dispose();
        var viewer = new ViewerViewModel(this, grid)
        {
            PreviewPixels = ThumbnailService.NormalizeSize((int)Math.Clamp(Shell?.LongestSidePixels ?? 2048, 1024, 2048)),
        };
        Viewer = viewer;
        await viewer.ShowAsync(index);
    }

    /// <summary>Shows a person's photos in the People tab (from the viewer's list of people in a photo).</summary>
    public async Task OpenPersonAsync(long personId)
    {
        CloseEditor();
        CloseViewer();
        SelectedTab = PeopleTab;
        if (People is { } people) await people.SelectPersonAsync(personId);
    }

    /// <summary>Opens the editor over the viewer for one of the item's files.</summary>
    public async Task OpenEditorAsync(Gallery.Core.Models.Asset asset, int rotation, double squeeze = 1)
    {
        if (Library is not { } lib) return;
        Editor?.Dispose();
        var editor = new EditorViewModel(this, lib, asset, rotation, squeeze);
        Editor = editor;
        await editor.LoadAsync();
    }

    /// <summary>Closes the editor. When it saved or exported something, the viewer and grids are updated to show it.</summary>
    public void CloseEditor()
    {
        if (Editor is not { } editor) return;
        Editor = null;
        editor.Dispose();
        if (!editor.Changed) return;
        if (Viewer is { } viewer) _ = viewer.ShowAsync(viewer.Index);
        _ = RefreshAllAsync();
    }

    public void CloseViewer()
    {
        if (Viewer is not { } v) return;
        Viewer = null;
        v.Dispose();
        v.Grid.Pin(-1);
        v.Grid.BringIntoView(v.Index);
    }

    /// <summary>Reads the library's status for the status bar, and refreshes the tabs when a scan has added or removed items.</summary>
    internal async Task PollAsync()
    {
        if (_polling || Library is not { } lib) return;
        _polling = true;
        try
        {
            LibraryStatus st;
            try { st = await Task.Run(() => lib.Status.Snapshot(includePaths: false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return; }
            if (lib != Library) return;
            ShowStatus(st);
            var scanning = st.Scan.Running;
            var changed = _lastItems >= 0 && st.Stats.Items != _lastItems;
            // Face grouping has just finished, so reload the People tab to show its suggestions and matches.
            var grouping = st.Faces?.Grouping == true;
            if (_wasGrouping && !grouping && People is { } people) await people.RefreshAsync();
            _wasGrouping = grouping;
            if (_wasScanning && !scanning) await RefreshAllAsync();
            else if (scanning && changed && AllPhotos?.Grid.Total < PhotoGridViewModel.PageSize && DateTime.UtcNow - _lastLiveRefresh > TimeSpan.FromSeconds(4))
            {
                // While a small library is being indexed, show new items as they are saved, at most every few seconds.
                _lastLiveRefresh = DateTime.UtcNow;
                await RefreshAllAsync();
            }
            else if (!scanning && changed) await RefreshAllAsync(); // files deleted or restored while idle
            _wasScanning = scanning;
            _lastItems = st.Stats.Items;
        }
        finally { _polling = false; }
    }

    private async Task RefreshAllAsync()
    {
        var tasks = new List<Task>();
        if (AllPhotos is { } all) tasks.Add(all.Grid.ReloadAsync(keepPosition: true));
        if (Albums is { } albums) tasks.Add(albums.RefreshAsync());
        if (Years is { } years) tasks.Add(years.RefreshAsync());
        if (People is { } people) tasks.Add(people.RefreshAsync());
        tasks.Add(SettingsPage.LoadSourcesAsync());
        await Task.WhenAll(tasks);
    }

    private void ShowStatus(LibraryStatus st)
    {
        var s = st.Stats;
        LibraryText = $"{s.Items:N0} items · {s.Assets:N0} files · {Format.Bytes(s.Bytes)} · {s.DuplicateAssets:N0} duplicates"
            + (s.Missing > 0 ? $" · {s.Missing:N0} missing" : "");
        IsBusy = st.Busy;
        var sc = st.Scan;
        var th = st.Thumbnails;
        var parts = new List<string>();
        if (sc.Running)
        {
            parts.Add(sc.Phase switch
            {
                "checking" => "Checking for deleted files…",
                "discovering" => $"Scanning · {sc.Discovered:N0} files found",
                "indexing" => $"Indexing {sc.Processed:N0} of {sc.Queued:N0} · {sc.Pending:N0} pending" + (sc.EtaSeconds is { } eta ? $" · {Eta(eta)}" : ""),
                _ => "Finishing the scan…",
            });
        }
        if (th.Running && th.Pending > 0) parts.Add($"Thumbnails {th.Done:N0} of {th.Total:N0}");
        if (st.Transcodes.Queued > 0) parts.Add($"Transcoding · {st.Transcodes.Queued:N0} queued");
        if (st.Exports is { Queued: > 0 } ex) parts.Add($"Exporting · {ex.Queued:N0} to go");
        if (st.Faces is { Running: true } fa) parts.Add(fa.Pending > 0 ? $"Faces {fa.Done:N0} of {fa.Done + fa.Pending:N0}" : "Checking faces…");
        else if (st.Faces is { Grouping: true }) parts.Add("Grouping faces into people…");
        else if (st.Faces?.State == "downloading") parts.Add("Downloading face models…");
        if (parts.Count == 0)
            parts.Add(sc.LastFinishedAt is { } last ? $"Scanned {last.Replace('T', ' ')[..Math.Min(16, last.Length)]}" + (sc.LastStatus is "done" or null ? "" : $" ({sc.LastStatus})") : "Ready");
        StatusText = string.Join("   ·   ", parts);
        SettingsPage.IsScanning = sc.Running;
        SettingsPage.ScanStatus = sc.Running ? parts[0] : StatusText;
        SettingsPage.ShowFaceActivity(st.Faces);
    }

    private static string Eta(double s) => s < 60 ? $"~{Math.Max(1, Math.Round(s))} s left" : s < 3600 ? $"~{Math.Round(s / 60)} min left" : $"~{s / 3600:0.0} h left";

    internal void SaveSettings()
    {
        try { Settings.Save(_settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"Could not save settings: {ex.Message}"; }
    }

    /// <summary>Stops the status timer; call on the UI thread before disposing.</summary>
    public void StopPolling() => _timer.Stop();

    public async ValueTask DisposeAsync()
    {
        if (Library is { } lib)
        {
            Library = null;
            await lib.DisposeAsync().ConfigureAwait(false);
        }
    }
}
