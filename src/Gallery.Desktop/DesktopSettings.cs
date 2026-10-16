using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Desktop;

/// <summary>
/// The desktop app's own settings, in a JSON file in the user's application data folder. The source folders and the
/// folder for transcodes and edits belong to the library, so they are kept in its database, as for the web server.
/// </summary>
public sealed class DesktopSettings
{
    /// <summary>The library folder: database and thumbnails. Same default as the web server, so both can open the same library.</summary>
    public string DataDir { get; set; } = "~/.gallery";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    /// <summary>Smallest thumbnail width in the grids. Cells grow to fill each row.</summary>
    public double ThumbSize { get; set; } = 180;
    /// <summary>The zoom that photos open at in the viewer: 0 fits them to the window, otherwise a share of their own pixels (1 = 100%).</summary>
    public double ViewerZoom { get; set; }
    /// <summary>The editor's last export choices, offered again (except "use as the main file", which is chosen each time).</summary>
    public ExportPreferences Export { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery", "desktop.json");

    public static DesktopSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), Json) ?? new DesktopSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new DesktopSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public DesktopSettings Clone()
    {
        var copy = (DesktopSettings)MemberwiseClone();
        copy.Export = Export.Clone();
        return copy;
    }

    public GalleryOptions ToOptions() => new() { DataDir = DataDir, FfmpegPath = FfmpegPath, FfprobePath = FfprobePath };
}

/// <summary>The last export options from the editor's export panel.</summary>
public sealed class ExportPreferences
{
    /// <summary>"original", a long edge in pixels ("3840", "2048", "1080"), "custom" or "percent".</summary>
    public string Size { get; set; } = "original";
    /// <summary>The long edge for "custom", or the percentage for "percent".</summary>
    public int SizeValue { get; set; } = 2048;
    public int Quality { get; set; } = 90;
    public bool FullChroma { get; set; } = true;
    public bool KeepLocation { get; set; } = true;
    public bool SaveToGallery { get; set; } = true;
    public bool SaveCopy { get; set; }
    /// <summary>Where the last copy was saved, so the save dialog starts there.</summary>
    public string? CopyFolder { get; set; }

    public ExportPreferences Clone() => (ExportPreferences)MemberwiseClone();
}

