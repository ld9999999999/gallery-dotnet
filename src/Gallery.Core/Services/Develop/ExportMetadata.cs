using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Xmp;
using IsRational = SixLabors.ImageSharp.Rational;

namespace Gallery.Core.Services.Develop;

/// <summary>
/// The metadata of an exported JPEG: all of the original's EXIF (RAW or not), its XMP and its IPTC. A 360° camera's lens
/// calibration (an Insta360's, in UserComment) and anything else a viewer or another program reads stays with the photo.
/// Left out: tags about how the original file is laid out (strips, tiles, sub-images, a RAW's sensor data and colour
/// profile), which would be wrong in a JPEG, and the location unless it is to be kept. The pixels are upright, so the
/// orientation is "normal", and the pixel size is the export's.
/// </summary>
public static class ExportMetadata
{
    /// <summary>EXIF goes in one JPEG segment of at most 64 KB; above this size, leave out a maker note to make room.</summary>
    internal const int MaxExifBytes = 60_000;

    /// <summary>Tags about the original file's layout, by number: TIFF image structure, sub-image pointers, old-style
    /// JPEG thumbnails, the XMP and ICC blocks of a TIFF, and TIFF/EP's sensor pattern.</summary>
    private static readonly HashSet<ushort> FileLayoutTags =
    [
        0x00FE, 0x00FF, 0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0107, 0x0108, 0x0109, 0x010A, 0x0111, 0x0115, 0x0116,
        0x0117, 0x0118, 0x0119, 0x011C, 0x0122, 0x0123, 0x0124, 0x0125, 0x013D, 0x0140, 0x0142, 0x0143, 0x0144, 0x0145,
        0x014A, 0x0152, 0x0153, 0x0154, 0x0155, 0x015B, 0x0200, 0x0201, 0x0202, 0x0203, 0x0205, 0x0206, 0x0207, 0x0208,
        0x0209, 0x0211, 0x0212, 0x0213, 0x0214, 0x02BC, 0x828D, 0x828E, 0x8769, 0x8773, 0x8825, 0xA005,
    ];

    /// <summary>DNG's own tags (DNGVersion on): colour matrices, black levels, crops of the sensor data and so on.</summary>
    private static bool IsDngTag(ushort tag) => tag is >= 0xC612 and <= 0xCFFF;

    /// <summary>The GPS tags (numbered from 0 in their own directory).</summary>
    private static bool IsGpsTag(ushort tag) => tag <= 0x001F;

    /// <summary>Gives <paramref name="target"/> the original's metadata, for a picture of the given size.</summary>
    public static void Apply(ImageMetadata target, string originalPath, bool keepLocation, int width, int height)
    {
        ImageInfo? info = null;
        try { info = Image.Identify(originalPath); }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or IOException) { }
        var original = info?.Metadata.ExifProfile ?? info?.FrameMetadataCollection.FirstOrDefault()?.ExifProfile;
        target.ExifProfile = original is null ? Selected(originalPath, keepLocation) : Copy(original, keepLocation);
        // The image library drops a UserComment whose character set it doesn't know, such as the "INSTA360" an Insta360
        // camera writes in its DNGs, and with it the lens calibration the 360° view needs. It is written as ASCII.
        if (original is not null && !target.ExifProfile.TryGetValue(ExifTag.UserComment, out _) && Comment(originalPath) is { } comment)
            target.ExifProfile.SetValue(ExifTag.UserComment, new EncodedString(EncodedString.CharacterCode.ASCII, comment));
        Finish(target.ExifProfile, width, height);
        // The encoder writes the image's resolution over the EXIF's, so it has to be the original's too.
        if (target.ExifProfile.TryGetValue(ExifTag.XResolution, out var x) && target.ExifProfile.TryGetValue(ExifTag.YResolution, out var y)
            && x.Value.Denominator != 0 && y.Value.Denominator != 0)
        {
            target.HorizontalResolution = x.Value.ToDouble();
            target.VerticalResolution = y.Value.ToDouble();
            target.ResolutionUnits = target.ExifProfile.TryGetValue(ExifTag.ResolutionUnit, out var unit) && unit.Value == 3
                ? PixelResolutionUnit.PixelsPerCentimeter : PixelResolutionUnit.PixelsPerInch;
        }
        if (info?.Metadata.XmpProfile is { } xmp) target.XmpProfile = CleanXmp(xmp, keepLocation);
        if (info?.Metadata.IptcProfile is { } iptc) target.IptcProfile = iptc.DeepClone();
    }

    /// <summary>Everything in the original's EXIF but its file layout (and the location, unless kept).</summary>
    private static ExifProfile Copy(ExifProfile original, bool keepLocation)
    {
        var exif = original.DeepClone();
        foreach (var value in exif.Values.ToList())
        {
            var tag = (ushort)value.Tag;
            if (FileLayoutTags.Contains(tag) || IsDngTag(tag) || (!keepLocation && IsGpsTag(tag))) exif.RemoveValue(value.Tag);
        }
        return exif;
    }

    private static void Finish(ExifProfile exif, int width, int height)
    {
        exif.SetValue(ExifTag.Orientation, (ushort)1);
        if (!exif.TryGetValue(ExifTag.Software, out var software) || string.IsNullOrWhiteSpace(software.Value)) exif.SetValue(ExifTag.Software, "Gallery");
        exif.SetValue(ExifTag.PixelXDimension, (uint)width);
        exif.SetValue(ExifTag.PixelYDimension, (uint)height);
        if (exif.ToByteArray()?.Length > MaxExifBytes) exif.RemoveValue(ExifTag.MakerNote);
    }

    /// <summary>
    /// The original's XMP without what would now be wrong: its orientation (the pixels are upright) and, unless kept, its
    /// location.
    /// </summary>
    private static XmpProfile? CleanXmp(XmpProfile xmp, bool keepLocation)
    {
        XDocument? doc;
        try { doc = xmp.GetDocument(); }
        catch (System.Xml.XmlException) { return null; }
        if (doc is null) return null;
        bool Stale(XName name) =>
            (name.NamespaceName == "http://ns.adobe.com/tiff/1.0/" && name.LocalName == "Orientation")
            || (!keepLocation && name.NamespaceName == "http://ns.adobe.com/exif/1.0/" && name.LocalName.StartsWith("GPS", StringComparison.Ordinal));
        foreach (var a in doc.Descendants().Attributes().Where(a => Stale(a.Name)).ToList()) a.Remove();
        foreach (var e in doc.Descendants().Where(e => Stale(e.Name)).ToList()) e.Remove();
        using var bytes = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(bytes, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new System.Text.UTF8Encoding(false) }))
            doc.Save(writer);
        return new XmpProfile(bytes.ToArray());
    }

    /// <summary>
    /// For an original the image library can't read (a CR3, say): the main tags, read with MetadataExtractor: camera, lens,
    /// exposure, date, comment (which holds an Insta360's lens calibration) and the location unless it is to be left out.
    /// </summary>
    private static ExifProfile Selected(string originalPath, bool keepLocation)
    {
        var exif = new ExifProfile();
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(originalPath); }
        catch (Exception ex) when (ex is MetadataExtractor.ImageProcessingException or IOException) { return exif; }

        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        void Text(MetadataExtractor.Directory? d, int tag, ExifTag<string> to)
        {
            if (d?.GetString(tag) is { Length: > 0 } v) exif.SetValue(to, v.Trim());
        }
        void Ratio(MetadataExtractor.Directory? d, int tag, ExifTag<IsRational> to)
        {
            if (d is not null && d.TryGetRational(tag, out var r) && r.Denominator != 0 && r.Numerator >= 0)
                exif.SetValue(to, new IsRational((uint)r.Numerator, (uint)r.Denominator));
        }
        Text(ifd0, ExifDirectoryBase.TagMake, ExifTag.Make);
        Text(ifd0, ExifDirectoryBase.TagModel, ExifTag.Model);
        Text(ifd0, ExifDirectoryBase.TagArtist, ExifTag.Artist);
        Text(ifd0, ExifDirectoryBase.TagCopyright, ExifTag.Copyright);
        Text(ifd0, ExifDirectoryBase.TagImageDescription, ExifTag.ImageDescription);
        Text(sub, ExifDirectoryBase.TagDateTimeOriginal, ExifTag.DateTimeOriginal);
        Text(sub, ExifDirectoryBase.TagDateTimeDigitized, ExifTag.DateTimeDigitized);
        Text(sub, ExifDirectoryBase.TagLensModel, ExifTag.LensModel);
        Ratio(sub, ExifDirectoryBase.TagExposureTime, ExifTag.ExposureTime);
        Ratio(sub, ExifDirectoryBase.TagFNumber, ExifTag.FNumber);
        Ratio(sub, ExifDirectoryBase.TagFocalLength, ExifTag.FocalLength);
        if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso) && iso > 0)
            exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)Math.Min(iso, ushort.MaxValue)]);
        if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, out var f35) && f35 > 0)
            exif.SetValue(ExifTag.FocalLengthIn35mmFilm, (ushort)Math.Min(f35, ushort.MaxValue));
        if (sub is not null && DualFisheye.UserComment(sub)?.Trim() is { Length: > 0 } comment)
            exif.SetValue(ExifTag.UserComment, new EncodedString(EncodedString.CharacterCode.ASCII, comment));

        if (keepLocation && dirs.OfType<GpsDirectory>().FirstOrDefault() is { } gps && gps.TryGetGeoLocation(out var where) && !where.IsZero)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, where.Latitude >= 0 ? "N" : "S");
            exif.SetValue(ExifTag.GPSLatitude, Dms(Math.Abs(where.Latitude)));
            exif.SetValue(ExifTag.GPSLongitudeRef, where.Longitude >= 0 ? "E" : "W");
            exif.SetValue(ExifTag.GPSLongitude, Dms(Math.Abs(where.Longitude)));
            if (gps.TryGetRational(GpsDirectory.TagAltitude, out var alt) && alt.Denominator != 0)
            {
                exif.SetValue(ExifTag.GPSAltitude, new IsRational((uint)Math.Abs(alt.Numerator), (uint)Math.Abs(alt.Denominator)));
                exif.SetValue(ExifTag.GPSAltitudeRef, (byte)(gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var below) ? below : 0));
            }
        }
        return exif;
    }

    /// <summary>The original's UserComment as MetadataExtractor reads it, or null when it has none (or only blanks).</summary>
    private static string? Comment(string originalPath)
    {
        try
        {
            return ImageMetadataReader.ReadMetadata(originalPath).OfType<ExifSubIfdDirectory>()
                .Select(d => DualFisheye.UserComment(d)?.Trim('\0', ' ')).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        }
        catch (Exception ex) when (ex is MetadataExtractor.ImageProcessingException or IOException) { return null; }
    }

    /// <summary>Decimal degrees as degrees, minutes and seconds (to 1/1000 s).</summary>
    private static IsRational[] Dms(double degrees)
    {
        var d = Math.Floor(degrees);
        var m = Math.Floor((degrees - d) * 60);
        var s = (degrees - d - m / 60) * 3600;
        return [new IsRational((uint)d, 1), new IsRational((uint)m, 1), new IsRational((uint)Math.Round(s * 1000), 1000)];
    }
}
