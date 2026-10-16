using Gallery.Core;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gallery.Desktop.Services;

/// <summary>
/// One open library: the index, scanner, thumbnail queue and file watcher for a data folder, running in this process
/// just as they do in the web server. To open another data folder, dispose this one and start a new one.
/// </summary>
public sealed class LibraryHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly bool _started;

    private LibraryHost(IHost host, bool started)
    {
        _host = host;
        _started = started;
        Items = Get<ItemRepository>();
        Albums = Get<AlbumService>();
        Thumbnails = Get<ThumbnailService>();
        Metadata = Get<MetadataService>();
        Scanner = Get<Scanner>();
        Status = Get<StatusService>();
        Sources = Get<SourceService>();
        Settings = Get<SettingsService>();
        Options = Get<GalleryOptions>();
        Db = Get<Db>();
        Changes = Get<FileChangeService>();
        Develop = Get<Core.Services.Develop.DevelopService>();
        Edits = Get<EditService>();
        Recipes = Get<RecipeService>();
        Exports = Get<Core.Services.Develop.ExportService>();
        Faces = Get<Core.Services.Faces.FaceService>();
        FaceModels = Get<Core.Services.Faces.FaceModels>();
        FaceRepository = Get<Core.Services.Faces.FaceRepository>();
        People = Get<Core.Services.Faces.PeopleService>();
        FaceCrops = Get<Core.Services.Faces.FaceCrops>();
        Seams = Get<SeamService>();
        Loader = new ThumbnailLoader(Thumbnails, Items);
    }

    public ItemRepository Items { get; }
    public AlbumService Albums { get; }
    public ThumbnailService Thumbnails { get; }
    public MetadataService Metadata { get; }
    public Scanner Scanner { get; }
    public StatusService Status { get; }
    public SourceService Sources { get; }
    public SettingsService Settings { get; }
    public GalleryOptions Options { get; }
    public Db Db { get; }
    public FileChangeService Changes { get; }
    /// <summary>Editing copies of photos (RAWs decoded by LibRaw) for the editor.</summary>
    public Core.Services.Develop.DevelopService Develop { get; }
    public EditService Edits { get; }
    public RecipeService Recipes { get; }
    /// <summary>JPEG exports, one at a time in the background (started with the other services).</summary>
    public Core.Services.Develop.ExportService Exports { get; }
    /// <summary>Finding faces in the background (started with the other services), its model packs and its tables.</summary>
    public Core.Services.Faces.FaceService Faces { get; }
    public Core.Services.Faces.FaceModels FaceModels { get; }
    public Core.Services.Faces.FaceRepository FaceRepository { get; }
    /// <summary>People: grouping faces (in the background), corrections, and the pictures of faces.</summary>
    public Core.Services.Faces.PeopleService People { get; }
    public Core.Services.Faces.FaceCrops FaceCrops { get; }
    /// <summary>How far away things are along the seam of a 360° photo saved as two fisheye circles, to line them up.</summary>
    public SeamService Seams { get; }
    /// <summary>Decoded thumbnails shared by the grids and album lists of this library.</summary>
    public ThumbnailLoader Loader { get; }

    private T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>
    /// Opens the library in options.DataDir, and creates it if it is new. With startServices false, nothing runs in the
    /// background (no scan, watcher or thumbnail queue), so tests can run a scan themselves.
    /// </summary>
    public static async Task<LibraryHost> OpenAsync(GalleryOptions options, bool startServices = true)
    {
        // Configuration comes only from DesktopSettings.
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        b.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        b.Logging.SetMinimumLevel(LogLevel.Information);
        b.Services.AddGalleryCore(options);
        var host = b.Build();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Gallery.Desktop");
        await Task.Run(() =>
        {
            GalleryCore.Prepare(host.Services, log);
            if (startServices) GalleryCore.QueueStartupScan(host.Services, log);
        });
        if (startServices) await host.StartAsync();
        return new LibraryHost(host, startServices);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _host.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        }
        _host.Dispose();
    }
}
