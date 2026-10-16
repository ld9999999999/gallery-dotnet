using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Gallery.Core.Services;

public sealed record MetaField(string Label, string Value);
public sealed record MetaTag(string Name, string Value);
public sealed record MetaGroup(string Name, List<MetaTag> Tags);
public sealed record GeoPoint(double Lat, double Lon, double? AltitudeM);

/// <summary>What the info panel shows: a short summary of selected fields, the location if there is one, and every tag grouped by source.</summary>
public sealed record AssetMetadata(List<MetaField> Summary, GeoPoint? Location, List<MetaGroup> Groups);

/// <summary>Turns MetadataExtractor directories (images, RAW) or ffprobe JSON (video, audio) into <see cref="AssetMetadata"/>.</summary>
public static class MetadataDetails
{
    private const int MaxValueLength = 300;
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "File", "Huffman" };

    // ---------------------------------------------------------------- images (EXIF and similar)

    public static AssetMetadata FromDirectories(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        var summary = new List<MetaField>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) summary.Add(new MetaField(label, value.Trim())); }

        var make = Desc(ifd0, ExifDirectoryBase.TagMake);
        var model = Desc(ifd0, ExifDirectoryBase.TagModel);
        Add("Camera", model is not null && make is not null && !model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model ?? make);
        Add("Lens", Desc(sub, ExifDirectoryBase.TagLensModel) ?? Desc(sub, ExifDirectoryBase.TagLensMake));
        Add("Exposure", Join(Desc(sub, ExifDirectoryBase.TagFNumber), Desc(sub, ExifDirectoryBase.TagExposureTime),
            Desc(sub, ExifDirectoryBase.TagIsoEquivalent) is { } iso ? $"ISO {iso}" : null));
        var focal = Desc(sub, ExifDirectoryBase.TagFocalLength);
        var focal35 = Desc(sub, ExifDirectoryBase.Tag35MMFilmEquivFocalLength);
        Add("Focal length", focal is null ? null : focal35 is null || focal35 == focal ? focal : $"{focal} ({focal35} in 35 mm)");
        var bias = Desc(sub, ExifDirectoryBase.TagExposureBias);
        if (bias is not null && !Regex.IsMatch(bias, @"^[+-]?0(\.0+)? EV$")) Add("Exposure bias", bias);
        Add("Mode", Desc(sub, ExifDirectoryBase.TagExposureProgram));
        Add("Metering", Desc(sub, ExifDirectoryBase.TagMeteringMode));
        Add("Flash", Desc(sub, ExifDirectoryBase.TagFlash));
        Add("White balance", Desc(sub, ExifDirectoryBase.TagWhiteBalanceMode));
        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken))
            Add("Taken", taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (Desc(sub, ExifDirectoryBase.TagTimeZoneOriginal) is { } tz ? $" ({tz})" : ""));
        if (ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation) && orientation != 1)
            Add("Orientation", $"{Desc(ifd0, ExifDirectoryBase.TagOrientation)} · rotated for display");
        Add("Software", Desc(ifd0, ExifDirectoryBase.TagSoftware));
        Add("Artist", Desc(ifd0, ExifDirectoryBase.TagArtist));
        Add("Copyright", Desc(ifd0, ExifDirectoryBase.TagCopyright));
        Add("Description", Desc(ifd0, ExifDirectoryBase.TagImageDescription));
        Add("Serial no.", Desc(sub, ExifDirectoryBase.TagBodySerialNumber));

        GeoPoint? location = null;
        if (gps is not null && gps.TryGetGeoLocation(out var geo) && !geo.IsZero)
        {
            double? alt = gps.TryGetRational(GpsDirectory.TagAltitude, out var r) ? r.ToDouble() * (gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var aref) && aref == 1 ? -1 : 1) : null;
            location = new GeoPoint(Math.Round(geo.Latitude, 6), Math.Round(geo.Longitude, 6), alt is null ? null : Math.Round(alt.Value, 1));
        }

        var groups = dirs
            .Where(d => !SkippedDirectories.Contains(d.Name))
            .Select(d => new MetaGroup(d.Name, d.Tags
                .Where(t => t.HasName && !string.IsNullOrWhiteSpace(t.Description))
                .Select(t => new MetaTag(t.Name, Truncate(t.Description!.Trim())))
                .ToList()))
            .Where(g => g.Tags.Count > 0)
            .ToList();
        return new AssetMetadata(summary, location, groups);
    }

    private static string? Desc(MetadataExtractor.Directory? d, int tag)
    {
        if (d is null || !d.ContainsTag(tag)) return null;
        var s = d.GetDescription(tag)?.Trim().TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ---------------------------------------------------------------- video / audio (ffprobe)

    public static AssetMetadata FromProbeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var summary = new List<MetaField>();
        var groups = new List<MetaGroup>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) summary.Add(new MetaField(label, value.Trim())); }

        var formatTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("format", out var fmt))
        {
            if (fmt.TryGetProperty("tags", out var tags))
                foreach (var t in tags.EnumerateObject()) formatTags[t.Name] = t.Value.ToString();
            string? T(params string[] keys) => keys.Select(k => formatTags.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var make = T("com.apple.quicktime.make", "make", "com.android.manufacturer");
            var model = T("com.apple.quicktime.model", "model", "com.android.model");
            Add("Camera", model is not null && make is not null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model ?? make);
            Add("Title", T("title"));
            Add("Artist", T("artist", "album_artist"));
            Add("Album", T("album"));
            Add("Software", T("com.apple.quicktime.software", "software", "encoder"));

            var g = new List<MetaTag>();
            AddProp(g, fmt, "format_long_name", "Container");
            AddProp(g, fmt, "duration", "Duration (s)");
            if (fmt.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bps)) { g.Add(new MetaTag("Bitrate", FormatBitrate(bps))); Add("Bitrate", FormatBitrate(bps)); }
            AddProp(g, fmt, "size", "Size (bytes)");
            g.AddRange(formatTags.Select(kv => new MetaTag(kv.Key, Truncate(kv.Value))));
            groups.Add(new MetaGroup("Container", g));
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type") ?? "data";
                var index = s.TryGetProperty("index", out var ix) ? ix.GetInt32() : groups.Count;
                var attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                var g = new List<MetaTag>();
                foreach (var (key, label) in new[] { ("codec_long_name", "Codec"), ("profile", "Profile"), ("pix_fmt", "Pixel format"), ("color_space", "Colour space"),
                    ("sample_rate", "Sample rate"), ("channel_layout", "Channels"), ("bits_per_raw_sample", "Bits per sample"), ("duration", "Duration (s)") })
                    AddProp(g, s, key, label);
                if (s.TryGetProperty("width", out var w) && s.TryGetProperty("height", out var h)) g.Insert(1, new MetaTag("Resolution", $"{w.GetInt32()} × {h.GetInt32()}"));
                var fps = FrameRate(Str(s, "avg_frame_rate")) ?? FrameRate(Str(s, "r_frame_rate"));
                if (fps is not null && type == "video" && !attached) g.Add(new MetaTag("Frame rate", $"{fps:0.##} fps"));
                if (s.TryGetProperty("bit_rate", out var sbr) && long.TryParse(sbr.GetString(), out var sbps)) g.Add(new MetaTag("Bitrate", FormatBitrate(sbps)));
                var rotation = Rotation(s);
                if (rotation != 0) g.Add(new MetaTag("Rotation", RotationLabel(rotation)));
                if (s.TryGetProperty("tags", out var st))
                    foreach (var t in st.EnumerateObject()) g.Add(new MetaTag(t.Name, Truncate(t.Value.ToString())));
                groups.Add(new MetaGroup($"Stream {index} ({(attached ? "cover art" : type)})", g));

                if (type == "video" && !attached && !summary.Any(f => f.Label == "Video"))
                {
                    var res = s.TryGetProperty("width", out var vw) && s.TryGetProperty("height", out var vh) ? $"{vw.GetInt32()} × {vh.GetInt32()}" : null;
                    var codec = Str(s, "codec_name") is { } cn ? cn + (Str(s, "profile") is { } pr ? $" ({pr})" : "") : null;
                    Add("Video", Join(codec, res, fps is null ? null : $"{fps:0.##} fps"));
                    if (rotation != 0) Add("Rotation", $"{RotationLabel(rotation)} · rotated for display");
                }
                else if (type == "audio" && !summary.Any(f => f.Label == "Audio"))
                {
                    var rate = int.TryParse(Str(s, "sample_rate"), out var hz) ? $"{hz / 1000.0:0.#} kHz" : null;
                    Add("Audio", Join(Str(s, "codec_name"), rate, Str(s, "channel_layout")));
                }
            }
        }

        GeoPoint? location = ParseIso6709(formatTags.GetValueOrDefault("com.apple.quicktime.location.ISO6709") ?? formatTags.GetValueOrDefault("location"));
        // Most useful lines first, in a fixed order.
        var order = new[] { "Camera", "Title", "Artist", "Album", "Video", "Rotation", "Audio", "Bitrate", "Software" };
        summary = summary.OrderBy(f => Array.IndexOf(order, f.Label) is var i && i < 0 ? 99 : i).ToList();
        return new AssetMetadata(summary, location, groups.Where(g => g.Tags.Count > 0).ToList());
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static void AddProp(List<MetaTag> g, JsonElement e, string prop, string label)
    {
        var v = Str(e, prop);
        if (!string.IsNullOrWhiteSpace(v) && v != "unknown") g.Add(new MetaTag(label, Truncate(v)));
    }

    private static double? FrameRate(string? r)
    {
        if (r is null) return null;
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0) return n / d;
        return null;
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var rv)) return ((-rv % 360) + 360) % 360; // ffprobe reports counter-clockwise
        return 0;
    }

    private static string RotationLabel(int clockwise) => clockwise switch
    {
        90 => "90° clockwise",
        270 => "90° counter-clockwise",
        _ => $"{clockwise}°",
    };

    private static string FormatBitrate(long bps) => bps >= 1_000_000 ? $"{bps / 1_000_000.0:0.#} Mbit/s" : $"{bps / 1000.0:0} kbit/s";

    /// <summary>Parses ISO 6709 decimal-degree strings in the form phones write them, for example "+37.7858-122.4064+010.000/".</summary>
    public static GeoPoint? ParseIso6709(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)([+-]\d+(?:\.\d+)?)?");
        if (!m.Success) return null;
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || (lat == 0 && lon == 0)) return null;
        double? alt = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        return new GeoPoint(lat, lon, alt);
    }

    // ---------------------------------------------------------------- helpers

    private static string? Join(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return p.Count == 0 ? null : string.Join(" · ", p);
    }

    private static string Truncate(string s) => s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";
}
