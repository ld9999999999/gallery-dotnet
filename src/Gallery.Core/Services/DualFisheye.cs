using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;

namespace Gallery.Core.Services;

/// <summary>
/// Insta360 cameras save photos unstitched: each half of the 2:1 picture is the circle seen by one of the two
/// back-to-back fisheye lenses. The camera writes each lens's calibration into EXIF UserComment. The web viewer
/// stitches the two circles into a sphere while it draws (panorama.js), using the lens model below.
///
/// The calibration string ("offset") is read the way Gyroflow's telemetry-parser does it (src/insta360/rig.rs, plus the
/// lens model in the video-types crate, both MIT/Apache-2.0): <c>n_lens_…_flags</c>, where <c>flags &gt;&gt; 16</c> is
/// the layout version. In version 3, each lens has 19 numbers:
/// <c>xi fx fy cx cy yaw pitch roll tx ty tz k1 k2 k3 p1 p2 width height lens_type</c> (version 6 has 13 distortion
/// terms instead of 5). The model is the unified sphere (Mei) model: for a unit ray (X, Y, Z) in the lens's frame
/// (x right, y down, z along the lens), m = (X, Y) / (Z + xi), then radial and tangential distortion, then u = cx + fx·xd.
///
/// Calibration pixels are the photo's own pixels: the X5's 11904 × 5952 photo is the 10752 × 5376 calibration frame
/// plus 288 px around each circle. On two X5 photos, using the focal lengths as given leaves far things (ceilings, sky)
/// on each side of the seam about 2/255 apart in brightness. Scaling them by the larger photo size instead makes them
/// 8–9/255 apart and they show double. The two circles are crossfaded over a 6° band at the seam, like the Insta360
/// Studio default.
///
/// The lenses sit 3.2 cm apart (one behind the other), so something close to the camera is seen from two slightly
/// different places and doesn't line up at the seam by itself: floor tiles one metre away were about 2° apart.
/// To compensate, each lens is aimed at the point where the direction meets a given distance from the middle between
/// the lenses. That distance is estimated along the seam from the picture (<see cref="EstimateSeam"/>): 256 places
/// around it, each at the distance where the two lenses agree best, kept smooth from one place to the next. It applies
/// only near the seam (<see cref="InverseDistanceAt"/>): in full over the blend, where both lenses show and must agree,
/// then fading out over 10° beyond it. Further in, each circle is drawn exactly as its own lens saw it, because the
/// seam distances would bend straight edges (a wall, a window frame) that cross places with different distances.
/// </summary>
public static class DualFisheye
{
    /// <summary>One lens, in fractions of the whole picture, so any preview size works.</summary>
    /// <param name="Rotation">Turns a direction in the viewer's frame (x right, y up, the start view along −z) into the
    /// lens's frame (x right, y down, z along the lens), row by row.</param>
    /// <param name="Distortion">13 terms in the version 6 layout ("RadtanPro"): radial d0–d4, decentering d5–d8,
    /// thin prism d9–d12. Version 3's k1 k2 k3 p1 p2 map to d0 d1 d2 d6 d5.</param>
    /// <param name="Shift">From the lens to the middle between the lenses, in metres, in the viewer's frame. This lens
    /// sees a direction d at distance D from the middle along d + Shift / D.</param>
    public sealed record Lens(double Xi, double[] Centre, double[] Focal, double[] Rotation, double[] Distortion, double[] Shift);

    /// <param name="Blend">Width in degrees of the crossfade at the seam, centred on the circle between the lenses.</param>
    /// <param name="Fade">Degrees past the blend over which the seam distances fade out (<see cref="InverseDistanceAt"/>).</param>
    public sealed record Rig(IReadOnlyList<Lens> Lenses, double Blend, double Fade);

    public const double BlendDegrees = 6.0;
    public const double FadeDegrees = 10.0;

    /// <summary>The calibration string in a photo's EXIF, or null if nothing looks like one.</summary>
    public static string? FindCalibration(IEnumerable<Directory> dirs)
    {
        foreach (var sub in dirs.OfType<ExifSubIfdDirectory>())
        {
            var text = UserComment(sub)?.Trim();
            if (text is { Length: > 40 } && char.IsDigit(text[0]) && text[1] == '_') return text;
        }
        return null;
    }

    /// <summary>
    /// EXIF UserComment as text. The first 8 bytes normally name the character set ("ASCII", "UNICODE"…), but the camera
    /// writes "INSTA360" there in its DNGs (a JPEG from Capture One has "ASCII"). That isn't a real character set, so
    /// those 8 bytes are dropped and the rest is read as ASCII.
    /// </summary>
    public static string? UserComment(ExifSubIfdDirectory sub)
    {
        var text = sub.GetDescription(ExifDirectoryBase.TagUserComment);
        return text is not null && text.StartsWith(VendorMarker, StringComparison.OrdinalIgnoreCase) ? text[VendorMarker.Length..].Trim('\0', ' ') : text;
    }

    private const string VendorMarker = "INSTA360";

    /// <summary>The lenses of a photo, from its own metadata. Null if there is no readable calibration.</summary>
    public static Rig? Read(string path, int pictureWidth, int pictureHeight)
    {
        try { return FindCalibration(ImageMetadataReader.ReadMetadata(path)) is { } s ? Parse(s, pictureWidth, pictureHeight) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Whether a calibration string describes two lenses in a layout we can read (versions 3 and 6). An Insta360 photo
    /// with an unreadable string is still unstitched, so it can't be shown as an equirectangular 360° photo either.
    /// </summary>
    public static bool IsReadable(string calibration) => Parse(calibration, 2, 1) is not null;

    /// <summary>Reads a calibration string for a picture of the given size. Null if it can't be read.</summary>
    public static Rig? Parse(string calibration, int pictureWidth, int pictureHeight)
    {
        // Anything after the first space (a short second block of numbers) isn't lens calibration.
        var tokens = calibration.Trim().Split(' ')[0].Split('_');
        if (tokens.Length < 3 || !int.TryParse(tokens[0], out var count) || count != 2) return null;
        if (!long.TryParse(tokens[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)) return null;
        var perLens = (flags >> 16) switch { 3 => 19, 6 => 27, _ => 0 };
        if (perLens == 0 || tokens.Length != 1 + count * perLens + 1) return null;
        var values = new double[count * perLens];
        for (var i = 0; i < values.Length; i++)
            if (!double.TryParse(tokens[1 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i])) return null;
        if (pictureWidth <= 0 || pictureHeight <= 0) return null;

        var lenses = new List<Lens>();
        var baseRotation = VendorRotation(0, 0, 90, lensIndex: 0);
        // Each lens's position (its tx ty tz, in the rig's frame: x right, y down, z ahead) converted to the viewer's
        // frame, and the midpoint between them.
        var origins = Enumerable.Range(0, count).Select(i => new[] { values[i * perLens + 8], -values[i * perLens + 9], -values[i * perLens + 10] }).ToArray();
        var middle = Enumerable.Range(0, 3).Select(k => origins.Average(o => o[k])).ToArray();
        for (var i = 0; i < count; i++)
        {
            var v = values.AsSpan(i * perLens, perLens);
            double xi = v[0], fx = v[1], fy = v[2], cx = v[3], cy = v[4];
            var calW = v[perLens - 3] / count; // each lens's share of the calibration frame
            var calH = v[perLens - 2];
            if (xi < 0 || fx <= 0 || fy <= 0 || calW <= 0 || calH <= 0) return null;
            var distortion = new double[13];
            if (perLens == 19) // k1 k2 k3 p1 p2
            {
                distortion[0] = v[11]; distortion[1] = v[12]; distortion[2] = v[13];
                distortion[6] = v[14]; distortion[5] = v[15];
            }
            else v.Slice(11, 13).CopyTo(distortion);

            // When the photo's half is at least as large as the calibration frame, keep the calibration's pixel size and
            // centre it (tested on the X5's photos). A smaller picture is treated as scaled down from it (untested).
            var picW = (double)pictureWidth / count;
            var s = picW >= calW ? 1.0 : picW / calW;
            var u = s * (cx - i * calW) + (picW - s * calW) / 2 + i * picW;
            var vv = s * cy + (pictureHeight - s * calH) / 2;

            // Viewer frame to lens frame: viewer (x right, y up, z back) → rig (x right, y down, z forward, along the
            // first lens's axis) → lens. Rig-from-lens is base · vendor⁻¹ (telemetry-parser's `rotation`), so
            // lens-from-rig is vendor · baseᵀ.
            var lensFromRig = Multiply(VendorRotation(v[5], v[6], v[7], i), Transpose(baseRotation));
            var lensFromViewer = Multiply(lensFromRig, new double[] { 1, 0, 0, 0, -1, 0, 0, 0, -1 });
            lenses.Add(new Lens(xi, [u / pictureWidth, vv / pictureHeight], [s * fx / pictureWidth, s * fy / pictureHeight], lensFromViewer, distortion,
                [middle[0] - origins[i][0], middle[1] - origins[i][1], middle[2] - origins[i][2]]));
        }
        return new Rig(lenses, BlendDegrees, FadeDegrees);
    }

    /// <summary>
    /// Where a lens sees a direction in the viewer's frame, in fractions of the picture, plus the angle from its axis in
    /// degrees. <paramref name="inverseDistance"/> (1/metres, 0 for far away) says how close the seen thing is to the
    /// middle between the lenses. Same math as the fragment shader in panorama.js.
    /// </summary>
    public static (double U, double V, double AngleDeg) Project(Lens lens, double x, double y, double z, double inverseDistance = 0)
    {
        var (u, v, cz) = Spot(lens, x, y, z, inverseDistance);
        return (u, v, Math.Acos(Math.Clamp(cz, -1, 1)) * 180 / Math.PI);
    }

    /// <summary>
    /// <see cref="Project"/> without the angle, which would double the cost. Returns the spot and the cosine of the
    /// angle from the lens's axis. The desktop viewer uses it, since it needs the angle only near the seam.
    /// </summary>
    public static (double U, double V, double CosAngle) Spot(Lens lens, double x, double y, double z, double inverseDistance = 0)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        x = x / n + inverseDistance * lens.Shift[0]; y = y / n + inverseDistance * lens.Shift[1]; z = z / n + inverseDistance * lens.Shift[2];
        n = Math.Sqrt(x * x + y * y + z * z);
        var r = lens.Rotation;
        double cx = (r[0] * x + r[1] * y + r[2] * z) / n, cy = (r[3] * x + r[4] * y + r[5] * z) / n, cz = (r[6] * x + r[7] * y + r[8] * z) / n;
        var den = cz + lens.Xi;
        double mx = cx / den, my = cy / den, r2 = mx * mx + my * my;
        var d = lens.Distortion;
        var radial = 1 + r2 * (d[0] + r2 * (d[1] + r2 * (d[2] + r2 * (d[3] + r2 * d[4]))));
        double a = d[5] + d[7] * r2, b = d[6] + d[8] * r2;
        var xd = mx * radial + (r2 + 2 * mx * mx) * a + 2 * mx * my * b + d[9] * r2 + d[11] * r2 * r2;
        var yd = my * radial + (r2 + 2 * my * my) * b + 2 * mx * my * a + d[10] * r2 + d[12] * r2 * r2;
        return (lens.Centre[0] + lens.Focal[0] * xd, lens.Centre[1] + lens.Focal[1] * yd, cz);
    }

    /// <summary>
    /// The reverse of <see cref="Project"/> for a lens: the unit direction (viewer's frame, from the lens itself) seen at
    /// a spot of the picture (in fractions). Null outside what the model can see. Tests use it to build a picture of a
    /// known scene.
    /// </summary>
    public static double[]? Unproject(Lens lens, double u, double v)
    {
        double xd = (u - lens.Centre[0]) / lens.Focal[0], yd = (v - lens.Centre[1]) / lens.Focal[1];
        var d = lens.Distortion;
        double mx = xd, my = yd;
        for (var k = 0; k < 50; k++) // undo the distortion by fixed-point iteration
        {
            var r2 = mx * mx + my * my;
            var radial = 1 + r2 * (d[0] + r2 * (d[1] + r2 * (d[2] + r2 * (d[3] + r2 * d[4]))));
            double a = d[5] + d[7] * r2, b = d[6] + d[8] * r2;
            var ex = xd - (mx * radial + (r2 + 2 * mx * mx) * a + 2 * mx * my * b + d[9] * r2 + d[11] * r2 * r2);
            var ey = yd - (my * radial + (r2 + 2 * my * my) * b + 2 * mx * my * a + d[10] * r2 + d[12] * r2 * r2);
            mx += 0.8 * ex; my += 0.8 * ey;
        }
        var m2 = mx * mx + my * my;
        var disc = 1 + (1 - lens.Xi * lens.Xi) * m2;
        if (disc < 0) return null;
        var l = (lens.Xi + Math.Sqrt(disc)) / (1 + m2);
        double px = l * mx, py = l * my, pz = l - lens.Xi;
        var r = lens.Rotation; // the transpose turns it back
        return [r[0] * px + r[3] * py + r[6] * pz, r[1] * px + r[4] * py + r[7] * pz, r[2] * px + r[5] * py + r[8] * pz];
    }

    /// <summary>Places around the seam where a distance is estimated. The k-th is k · 360°/256 from the right, towards up.</summary>
    public const int SeamPlaces = 256;

    /// <summary>Distances tried: 1/metres from 0 (far) to 3 (33 cm), in steps of 0.05.</summary>
    public const double MaxInverseDistance = 3.0;
    private const double InverseDistanceStep = 0.05;

    /// <summary>
    /// How far away things are along the seam, as 1/metres for each of the <see cref="SeamPlaces"/> places, found from
    /// the picture's brightness (<paramref name="luminance"/>, row by row, any size, best slightly blurred).
    ///
    /// Steps:
    /// 1. At each place, take a patch of the seam 6° across (the blend) and 1.4° along (to the next place).
    /// 2. For each distance tried, look at the patch through both lenses. The mean brightness difference is the cost.
    /// 3. Pick the distances with the lowest total of: the costs; plus, between neighbouring places, 2 per 1/m of change
    ///    and 20 per (1/m)² (so a plain ceiling follows its neighbours instead of noise, and the distance moves in steps
    ///    of a few tenths rather than jumps); plus 0.5 per 1/m (so far wins where nothing else decides).
    /// 4. Do this with a Viterbi pass over three laps of the circle, and keep the middle lap.
    /// The squared term keeps steps under 0.7 and still lines up the floor below well; a linear term alone (8 per 1/m)
    /// lets the distance jump by up to 2.8 between neighbours.
    ///
    /// Tuned on two X5 photos. The places have to be this close together: on a shiny floor the tile joints are at the
    /// floor's distance, but the reflections are as far away as what they reflect. With 64 places (5.6° each) the bright
    /// reflections won whole stretches and left joints 10–23 px apart on screen; with 256, the two lenses' pictures of
    /// that floor are within 4 px. Seam differences: 8.0 (floor) with 256 places, 12.7 with 64, and 7.5 for the best
    /// distance at each place on its own.
    /// </summary>
    public static double[] EstimateSeam(Rig rig, float[] luminance, int width, int height)
    {
        const double linear = 2, squared = 20, preferFar = 0.5;
        var labels = (int)Math.Round(MaxInverseDistance / InverseDistanceStep) + 1;
        var across = Enumerable.Range(0, 25).Select(i => Math.Tan((-3 + 6.0 * i / 24) * Math.PI / 180)).ToArray();
        var half = 180.0 / SeamPlaces; // degrees: each patch reaches to the next place
        var along = Enumerable.Range(0, 9).Select(i => Math.Tan((-half + 2 * half * i / 8) * Math.PI / 180)).ToArray();
        var cost = new double[SeamPlaces, labels];
        Parallel.For(0, SeamPlaces, k =>
        {
            var phi = 2 * Math.PI * k / SeamPlaces;
            double sx = Math.Cos(phi), sy = Math.Sin(phi), tx = -sy, ty = sx;
            for (var j = 0; j < labels; j++)
            {
                var rho = j * InverseDistanceStep;
                double sum = 0;
                foreach (var a in across)
                    foreach (var b in along)
                    {
                        double x = sx + b * tx, y = sy + b * ty, z = a;
                        var p0 = Project(rig.Lenses[0], x, y, z, rho);
                        var p1 = Project(rig.Lenses[1], x, y, z, rho);
                        sum += Math.Abs(Sample(luminance, width, height, p0.U, p0.V) - Sample(luminance, width, height, p1.U, p1.V));
                    }
                cost[k, j] = sum / (across.Length * along.Length) + preferFar * rho;
            }
        });

        // Viterbi over three laps of the circle, so the first and last places see neighbours on both sides.
        var steps = 3 * SeamPlaces;
        var back = new int[steps, labels];
        var acc = new double[labels];
        for (var j = 0; j < labels; j++) acc[j] = cost[0, j];
        for (var i = 1; i < steps; i++)
        {
            var next = new double[labels];
            for (var j = 0; j < labels; j++)
            {
                var best = double.MaxValue;
                for (var q = 0; q < labels; q++)
                {
                    var change = Math.Abs(q - j) * InverseDistanceStep;
                    var c = acc[q] + linear * change + squared * change * change;
                    if (c < best) { best = c; back[i, j] = q; }
                }
                next[j] = best + cost[i % SeamPlaces, j];
            }
            acc = next;
        }
        var label = Array.IndexOf(acc, acc.Min());
        var profile = new double[SeamPlaces];
        for (var i = steps - 1; i >= 0; i--)
        {
            if (i >= SeamPlaces && i < 2 * SeamPlaces) profile[i - SeamPlaces] = label * InverseDistanceStep;
            if (i > 0) label = back[i, label];
        }
        return profile;
    }

    /// <summary>
    /// The inverse distance (1/metres) to draw a direction in the viewer's frame at: the seam's <paramref name="profile"/>
    /// at the direction's place around the seam (interpolated linearly between places). Full strength over the blend,
    /// then a smoothstep fade over <see cref="Rig.Fade"/> degrees past it, and zero further in. Same math as the
    /// fragment shader.
    /// </summary>
    public static double InverseDistanceAt(Rig rig, double[] profile, double x, double y, double z)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        var fromSeam = Math.Abs(Math.Asin(Math.Clamp(z / n, -1, 1))) * 180 / Math.PI;
        var t = Math.Clamp((fromSeam - rig.Blend / 2) / rig.Fade, 0, 1);
        var keep = 1 - t * t * (3 - 2 * t);
        if (keep == 0) return 0;
        var places = profile.Length;
        var p = (Math.Atan2(y, x) / (2 * Math.PI) + 1) % 1 * places;
        var k = (int)Math.Floor(p) % places;
        return keep * (profile[k] + (profile[(k + 1) % places] - profile[k]) * (p - Math.Floor(p)));
    }

    /// <summary>Bilinear sample of a brightness picture at a spot given as fractions of its size.</summary>
    private static double Sample(float[] lum, int w, int h, double u, double v)
    {
        double fx = u * w - 0.5, fy = v * h - 0.5;
        int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 2), y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 2);
        double ax = Math.Clamp(fx - x0, 0, 1), ay = Math.Clamp(fy - y0, 0, 1);
        var i = y0 * w + x0;
        return lum[i] * (1 - ax) * (1 - ay) + lum[i + 1] * ax * (1 - ay) + lum[i + w] * (1 - ax) * ay + lum[i + w + 1] * ax * ay;
    }

    /// <summary>
    /// telemetry-parser's <c>vendor_rotation</c>: R = Ry(pitch + 90°)·Rz(yaw)·Rx(roll), with angles in degrees. The
    /// first of two lenses gets an extra half turn about z, since the string omits the 180° between the lenses. Maps
    /// the vendor's world into the lens. Row-major 3×3.
    /// </summary>
    private static double[] VendorRotation(double yawDeg, double pitchDeg, double rollDeg, int lensIndex)
    {
        const double toRad = Math.PI / 180;
        var r = Multiply(Multiply(RotY(pitchDeg * toRad + Math.PI / 2), RotZ(yawDeg * toRad)), RotX(rollDeg * toRad));
        return lensIndex == 0 ? Multiply(r, RotZ(Math.PI)) : r;
    }

    private static double[] RotX(double a) { double c = Math.Cos(a), s = Math.Sin(a); return [1, 0, 0, 0, c, -s, 0, s, c]; }
    private static double[] RotY(double a) { double c = Math.Cos(a), s = Math.Sin(a); return [c, 0, s, 0, 1, 0, -s, 0, c]; }
    private static double[] RotZ(double a) { double c = Math.Cos(a), s = Math.Sin(a); return [c, -s, 0, s, c, 0, 0, 0, 1]; }

    private static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return m;
    }

    private static double[] Transpose(double[] a) => [a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8]];
}
