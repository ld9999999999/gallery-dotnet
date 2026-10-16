using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>Sorts files by extension. This is the single list of what gets indexed.</summary>
public static class FileTypes
{
    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "ptx",
        "raw", "rwl", "3fr", "fff", "iiq", "mos", "mrw", "x3f", "erf", "kdc", "dcr", "srw", "mef"
    };

    public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "jxl"
    };

    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "mts", "m2ts", "ts", "mpg", "mpeg", "3gp", "flv", "mxf", "vob", "divx"
    };

    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "opus", "wma", "aiff", "aif", "alac", "ape", "mka", "dsf"
    };

    /// <summary>Containers and codecs browsers play natively, so no transcoding is needed.</summary>
    public static readonly HashSet<string> BrowserVideoContainers = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "webm" };
    public static readonly HashSet<string> BrowserVideoCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264", "vp8", "vp9", "av1" };
    public static readonly HashSet<string> BrowserAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le" };
    public static readonly HashSet<string> BrowserAudioContainers = new(StringComparer.OrdinalIgnoreCase) { "mp3", "wav", "ogg", "oga", "opus", "m4a", "aac", "flac" };

    /// <summary>Returns (kind, isRaw), or null for an extension we don't index.</summary>
    public static (string Kind, bool IsRaw)? Classify(string ext)
    {
        ext = ext.TrimStart('.');
        if (Raw.Contains(ext)) return (MediaKind.Image, true);
        if (Image.Contains(ext)) return (MediaKind.Image, false);
        if (Video.Contains(ext)) return (MediaKind.Video, false);
        if (Audio.Contains(ext)) return (MediaKind.Audio, false);
        return null;
    }

    public static string MimeFor(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "jpe" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "webp" => "image/webp",
        "tif" or "tiff" => "image/tiff",
        "heic" or "heif" => "image/heic",
        "avif" => "image/avif",
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        "mkv" => "video/x-matroska",
        "avi" => "video/x-msvideo",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "flac" => "audio/flac",
        "ogg" or "oga" => "audio/ogg",
        "opus" => "audio/ogg",
        "m4a" or "aac" => "audio/mp4",
        _ => "application/octet-stream",
    };
}
