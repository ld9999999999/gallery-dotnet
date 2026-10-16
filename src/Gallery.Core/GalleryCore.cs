using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Core;

/// <summary>Sets up the library services the same way for the web server and the desktop app.</summary>
public static class GalleryCore
{
    /// <summary>Registers the index, scanner, thumbnail, transcode, album, source, watcher and face services, hosted ones included.</summary>
    public static IServiceCollection AddGalleryCore(this IServiceCollection services, GalleryOptions options)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        var dataDir = options.ResolvedDataDir;
        Directory.CreateDirectory(dataDir);
        services.AddSingleton(options);
        services.AddSingleton(new Db(dataDir));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Ffmpeg>();
        services.AddSingleton<HeifDecoder>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<ThumbnailService>();
        services.AddHostedService(sp => sp.GetRequiredService<ThumbnailService>());
        services.AddSingleton<TranscodeService>();
        services.AddSingleton<ItemRepository>();
        services.AddSingleton<AlbumService>();
        services.AddSingleton<Scanner>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<FileChangeService>();
        services.AddSingleton<LibraryWatcher>();
        services.AddSingleton<SourceService>();
        services.AddSingleton<Services.Develop.RawTools>();
        services.AddSingleton<Services.Develop.DevelopService>();
        services.AddSingleton<EditService>();
        services.AddSingleton<RecipeService>();
        services.AddSingleton<Services.Develop.ExportService>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Develop.ExportService>());
        services.AddSingleton<BackgroundCpu>();
        services.AddSingleton<Services.Faces.FaceModels>();
        services.AddSingleton<Services.Faces.FaceRepository>();
        services.AddSingleton<Services.Faces.FaceService>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Faces.FaceService>());
        services.AddSingleton<Services.Faces.PeopleService>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.Faces.PeopleService>());
        services.AddSingleton<Services.Faces.FaceCrops>();
        services.AddSingleton<HeicRepair>();
        services.AddHostedService(sp => sp.GetRequiredService<HeicRepair>());
        services.AddSingleton<SeamService>();
        services.AddSingleton<MemoryReport>();
        services.AddSingleton<CollageService>();
        services.AddSingleton<ProjectionBackfill>();
        services.AddHostedService(sp => sp.GetRequiredService<ProjectionBackfill>());
        services.AddHostedService(sp => sp.GetRequiredService<LibraryWatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<Scanner>());
        services.AddHostedService<ProgressLogger>();
        return services;
    }

    /// <summary>Updates the database, and adds default settings and the configured source folders. Call before the host starts.</summary>
    public static void Prepare(IServiceProvider sp, ILogger log)
    {
        var options = sp.GetRequiredService<GalleryOptions>();
        var db = sp.GetRequiredService<Db>();
        db.Migrate();
        sp.GetRequiredService<SettingsService>().SeedDefaults();
        using (var c = db.Open())
        {
            foreach (var s in options.Sources)
                c.Execute("INSERT OR IGNORE INTO sources(path, enabled, added_at) VALUES(@path,1,@now)", new { path = GalleryOptions.ExpandHome(s), now = Clock.NowIso() });
        }
        log.LogInformation("Data directory: {DataDir}  (db: {Db})", options.ResolvedDataDir, db.DbPath);
        if (!sp.GetRequiredService<Ffmpeg>().IsAvailable)
            log.LogWarning("ffmpeg/ffprobe not found – video & audio support is limited. Set Gallery:FfmpegPath / Gallery:FfprobePath.");
    }

    /// <summary>
    /// Starts a scan when the index is empty, goes on with one that the last run did not finish (shutdown, crash), or
    /// reads again the files whose dates were stored without using dates in file names.
    /// </summary>
    public static void QueueStartupScan(IServiceProvider sp, ILogger log)
    {
        var db = sp.GetRequiredService<Db>();
        var scanner = sp.GetRequiredService<Scanner>();
        using var c = db.Open();
        var resume = scanner.RecoverInterruptedRuns();
        var sourceCount = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sources WHERE enabled=1");
        var assetCount = c.ExecuteScalar<long>("SELECT COUNT(*) FROM assets");
        log.LogInformation("Index: {Assets:N0} files from {Sources} enabled source(s)", assetCount, sourceCount);
        if (sourceCount == 0)
        {
            log.LogWarning("No source directories configured. Add one in the settings, or pass --Gallery:Sources:0=/path/to/photos");
            return;
        }
        var dateReread = resume || assetCount == 0 ? 0 : scanner.CountFilesNeedingDateReread();
        if (!resume && assetCount > 0 && dateReread == 0) return;
        if (assetCount == 0 && !resume) log.LogInformation("Index is empty; starting the first scan");
        if (dateReread > 0)
            log.LogInformation("{Count:N0} files have a date to read again (a date at the start of their name, one in the future, or an AVI's header); scanning to re-read them", dateReread);
        scanner.RequestScan();
    }
}
