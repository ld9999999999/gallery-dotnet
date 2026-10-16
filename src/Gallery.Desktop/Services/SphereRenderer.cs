using System.Runtime.InteropServices;
using Gallery.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Desktop.Services;

/// <summary>
/// Draws a 360° photo as the view from the middle of the sphere around the camera, turned and zoomed, like the web
/// viewer (panorama.js), but on the CPU cores instead of a graphics card. For each pixel: find its view direction ->
/// read the spot of the picture it sees (bilinear). Two kinds of picture:
/// <list type="bullet">
/// <item>Equirectangular: the whole sphere, 360° across and 180° from top to bottom. The direction's longitude and
/// latitude give the spot, and the left and right edges meet.</item>
/// <item>Two fisheye circles side by side, as an Insta360 camera saves them: each lens's calibration
/// (<see cref="DualFisheye.Rig"/>) gives where it sees the direction, through <see cref="DualFisheye.Project"/>. The
/// two are crossfaded over the blend at the seam, each aimed using the seam distances (<see cref="DualFisheye.InverseDistanceAt"/>)
/// once they are known. Same math as the web viewer's fragment shader, from the same code the seam estimate uses.</item>
/// </list>
/// The view: y is up, the start view looks along −z (the middle of the picture), and the field of view is across the
/// longer side. A render depends only on the view, so it can run on any thread.
/// </summary>
public sealed class SphereRenderer
{
    public const double StartFov = 90, MinFov = 30, MaxFov = 100;

    private readonly uint[] _texture; // BGRA, the way Avalonia's Bgra8888 bitmaps store it
    private readonly DualFisheye.Rig? _rig;

    public SphereRenderer(uint[] texture, int width, int height, DualFisheye.Rig? rig = null)
    {
        if (texture.Length != width * height) throw new ArgumentException("the texture does not match its size");
        _texture = texture;
        TextureWidth = width;
        TextureHeight = height;
        _rig = rig;
    }

    public int TextureWidth { get; }
    public int TextureHeight { get; }
    public bool IsFisheye => _rig is not null;
    /// <summary>1/distance (1/metres) at <see cref="DualFisheye.SeamPlaces"/> places around the seam; null means everything is far.</summary>
    public double[]? Seam { get; set; }

    /// <summary>A picture file (a preview of the photo) as a texture.</summary>
    public static SphereRenderer Load(string path, DualFisheye.Rig? rig = null)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Bgra32>(path);
        var pixels = new uint[image.Width * image.Height];
        image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));
        return new SphereRenderer(pixels, image.Width, image.Height, rig);
    }

    /// <summary>The camera's right, up and forward directions, for a turn right and up (degrees).</summary>
    public static (double[] Right, double[] Up, double[] Forward) Basis(double yaw, double pitch)
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
        double cy = Math.Cos(y), sy = Math.Sin(y), cp = Math.Cos(p), sp = Math.Sin(p);
        return ([cy, 0, sy], [-sp * sy, cp, sp * cy], [cp * sy, sp, -cp * cy]);
    }

    /// <summary>Tangents of half the field of view across and down a w × h view; <paramref name="fov"/> is across the longer side.</summary>
    public static (double X, double Y) Tangents(double fov, int w, int h)
    {
        var t = Math.Tan(fov * Math.PI / 360);
        return w >= h ? (t, t * h / w) : (t * w / h, t);
    }

    /// <summary>Where an equirectangular picture shows a direction: fractions across (longitude) and down (latitude).</summary>
    public static (double U, double V) LonLat(double x, double y, double z)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        return (Math.Atan2(x, -z) / (2 * Math.PI) + 0.5, Math.Acos(Math.Clamp(y / n, -1, 1)) / Math.PI);
    }

    /// <summary><see cref="LonLat"/> in single precision: about twice as fast, and a thousandth of a pixel off at 8192 across.</summary>
    private static (double U, double V) LonLatFast(float x, float y, float z) =>
        (MathF.Atan2(x, -z) / (2 * MathF.PI) + 0.5f, MathF.Acos(Math.Clamp(y / MathF.Sqrt(x * x + y * y + z * z), -1f, 1f)) / MathF.PI);

    /// <summary>
    /// Draws the view into <paramref name="output"/> (BGRA packed into ints, as bitmaps take them, <paramref name="stride"/>
    /// pixels per row), <paramref name="w"/> × <paramref name="h"/>, looking <paramref name="yaw"/> degrees right and
    /// <paramref name="pitch"/> up, with <paramref name="fov"/> degrees across the longer side. Rows are split between
    /// <paramref name="threads"/> threads.
    /// </summary>
    public void Render(int[] output, int w, int h, int stride, double yaw, double pitch, double fov, int threads = 1)
    {
        if (output.Length < (h - 1) * stride + w) throw new ArgumentException("the output is too small");
        var (right, up, fwd) = Basis(yaw, pitch);
        var (tx, ty) = Tangents(fov, w, h);
        var seam = Seam;
        void Row(int j)
        {
            var vy = (1 - 2 * (j + 0.5) / h) * ty;
            double bx = fwd[0] + vy * up[0], by = fwd[1] + vy * up[1], bz = fwd[2] + vy * up[2];
            var o = j * stride;
            for (var i = 0; i < w; i++)
            {
                var vx = (2 * (i + 0.5) / w - 1) * tx;
                double x = bx + vx * right[0], y = by + vx * right[1], z = bz + vx * right[2];
                output[o + i] = (int)(_rig is { } rig ? Stitched(rig, seam, x, y, z) : Sample(LonLatFast((float)x, (float)y, (float)z), wrap: true));
            }
        }
        if (threads > 1) Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = threads }, Row);
        else for (var j = 0; j < h; j++) Row(j);
    }

    /// <summary>
    /// Two fisheye circles: the first lens alone up to blend/2 before the seam (where both are 90° away), the second
    /// alone past it, crossfaded in between. Each is aimed at the seam distance at that place.
    /// </summary>
    private uint Stitched(DualFisheye.Rig rig, double[]? seam, double x, double y, double z)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        x /= n; y /= n; z /= n;
        var rho = seam is not null && Math.Abs(z) < SeamReach(rig) ? DualFisheye.InverseDistanceAt(rig, seam, x, y, z) : 0;
        // The angle from each lens's axis sets the mix. Far from the seam (one lens within 75° of the direction, the
        // other past 105°) only one lens is used, and the cosines show this without working out the angles.
        double c0 = AxisCosine(rig.Lenses[0], x, y, z, rho), c1 = AxisCosine(rig.Lenses[1], x, y, z, rho);
        double w0;
        if (c0 >= 0.25 && c1 <= -0.25) w0 = 1;
        else if (c1 >= 0.25 && c0 <= -0.25) w0 = 0;
        else w0 = Math.Clamp(0.5 + (Degrees(c1) - Degrees(c0)) * 0.5 / rig.Blend, 0, 1);
        if (w0 >= 1) return Sample(DualFisheye.Spot(rig.Lenses[0], x, y, z, rho), wrap: false);
        if (w0 <= 0) return Sample(DualFisheye.Spot(rig.Lenses[1], x, y, z, rho), wrap: false);
        return Mix(Sample(DualFisheye.Spot(rig.Lenses[1], x, y, z, rho), wrap: false), Sample(DualFisheye.Spot(rig.Lenses[0], x, y, z, rho), wrap: false), w0);
    }

    private static double Degrees(double cosine) => Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;

    /// <summary>How far from the seam's plane (as |z| of a unit direction) the seam distances still count: the blend plus the fade.</summary>
    private static double SeamReach(DualFisheye.Rig rig) => Math.Sin((rig.Blend / 2 + rig.Fade) * Math.PI / 180);

    /// <summary>The cosine of the angle between a lens's axis (its rotation's last row) and the direction it sees from its position.</summary>
    private static double AxisCosine(DualFisheye.Lens lens, double x, double y, double z, double rho)
    {
        double sx = x + rho * lens.Shift[0], sy = y + rho * lens.Shift[1], sz = z + rho * lens.Shift[2];
        var r = lens.Rotation;
        return (r[6] * sx + r[7] * sy + r[8] * sz) / Math.Sqrt(sx * sx + sy * sy + sz * sz);
    }

    private uint Sample((double U, double V, double CosAngle) spot, bool wrap) => Sample((spot.U, spot.V), wrap);

    /// <summary>The picture at a spot (fractions), bilinear. Across, the edges either meet (<paramref name="wrap"/>) or stop.</summary>
    private uint Sample((double U, double V) at, bool wrap)
    {
        int tw = TextureWidth, th = TextureHeight;
        double fx = at.U * tw - 0.5, fy = at.V * th - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int ax = (int)((fx - x0) * 256), ay = (int)((fy - y0) * 256);
        int x1 = x0 + 1, y1 = Math.Min(y0 + 1, th - 1);
        y0 = Math.Clamp(y0, 0, th - 1);
        y1 = Math.Max(y1, 0);
        if (wrap) { x0 = ((x0 % tw) + tw) % tw; x1 = ((x1 % tw) + tw) % tw; }
        else { x0 = Math.Clamp(x0, 0, tw - 1); x1 = Math.Clamp(x1, 0, tw - 1); }
        var t = _texture;
        uint p00 = t[y0 * tw + x0], p10 = t[y0 * tw + x1], p01 = t[y1 * tw + x0], p11 = t[y1 * tw + x1];
        uint result = 0xFF000000u;
        for (var shift = 0; shift < 24; shift += 8)
        {
            int c00 = (int)(p00 >> shift) & 0xFF, c10 = (int)(p10 >> shift) & 0xFF, c01 = (int)(p01 >> shift) & 0xFF, c11 = (int)(p11 >> shift) & 0xFF;
            var top = c00 * (256 - ax) + c10 * ax;
            var bottom = c01 * (256 - ax) + c11 * ax;
            result |= (uint)((top * (256 - ay) + bottom * ay + 32768) >> 16) << shift;
        }
        return result;
    }

    /// <summary>a·(1 − w) + b·w, channel by channel.</summary>
    private static uint Mix(uint a, uint b, double w)
    {
        var k = (int)Math.Round(w * 256);
        uint result = 0xFF000000u;
        for (var shift = 0; shift < 24; shift += 8)
        {
            int ca = (int)(a >> shift) & 0xFF, cb = (int)(b >> shift) & 0xFF;
            result |= (uint)((ca * (256 - k) + cb * k + 128) >> 8) << shift;
        }
        return result;
    }
}
