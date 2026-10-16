namespace Gallery.Core.Models;

public static class MediaKind
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
}

public sealed class Item
{
    public long Id { get; set; }
    public string GroupKey { get; set; } = "";
    public string Kind { get; set; } = MediaKind.Image;
    public string? TakenAt { get; set; }          // ISO-8601 local time, sortable
    public int? TakenYear { get; set; }
    public string Title { get; set; } = "";       // file stem of the primary asset
    public string DirName { get; set; } = "";     // name of the containing directory
    public long? PrimaryAssetId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSec { get; set; }
    public int AssetCount { get; set; }
    public int Hidden { get; set; }
    public string CreatedAt { get; set; } = "";
}

public sealed class Asset
{
    public long Id { get; set; }
    public long ItemId { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Ext { get; set; } = "";         // lower-case, no dot
    public long Size { get; set; }
    public long Mtime { get; set; }               // unix seconds
    public string Hash { get; set; } = "";        // xxh3-64 hex (sampled for very large files)
    public string Kind { get; set; } = MediaKind.Image;
    public int IsRaw { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSec { get; set; }
    public string? TakenAt { get; set; }
    public string? TakenSource { get; set; }      // metadata | filename | file | none; null when not recorded
    public string? Codec { get; set; }            // video codec name from ffprobe
    public string? AudioCodec { get; set; }
    public string? Container { get; set; }        // ffprobe format_name
    public long LastSeenScan { get; set; }
    public int Missing { get; set; }
    /// <summary>For a JPEG exported from the photo editor, the asset it was made from. Null for files found by scanning.</summary>
    public long? EditOf { get; set; }
    /// <summary>"equirectangular" for a 360° photo (see <see cref="Services.Projection"/>), null for a flat one.</summary>
    public string? Projection { get; set; }
}

public sealed class ItemTag
{
    public long ItemId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class Album
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "manual";  // manual | auto
    public string? AutoDirName { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public long? CoverItemId { get; set; }
    /// <summary>The cover an admin chose, used while it is in the album and visible (else the earliest item is the cover).</summary>
    public long? ChosenCoverId { get; set; }
    public int ItemCount { get; set; }
    public string CreatedAt { get; set; } = "";
    /// <summary>1 when an admin has hidden the album (and everything in it) from viewers.</summary>
    public int ViewerHidden { get; set; }
    /// <summary>The cover's file, its hash, and the item's rotation and squeeze, for a thumbnail URL (set by <c>AlbumService.All</c>).</summary>
    public long? CoverAssetId { get; set; }
    public string? CoverHash { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? CoverRotationTag { get; set; }
    public int CoverRotation => Services.Rotation.Parse(CoverRotationTag);
    [System.Text.Json.Serialization.JsonIgnore] public string? CoverSqueezeTag { get; set; }
    public double CoverSqueeze => Services.Squeeze.Parse(CoverSqueezeTag);
}

public sealed class Source
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public int Enabled { get; set; } = 1;
    public string AddedAt { get; set; } = "";
}

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string Role { get; set; } = "viewer"; // admin | viewer
    public string CreatedAt { get; set; } = "";
}

public sealed class ScanRun
{
    public long Id { get; set; }
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public long FilesSeen { get; set; }
    public long FilesAdded { get; set; }
    public long FilesUpdated { get; set; }
    public long FilesMissing { get; set; }
    public string Status { get; set; } = "running"; // running | done | failed | cancelled
    public string? Message { get; set; }
}
