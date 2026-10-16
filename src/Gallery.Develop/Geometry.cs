namespace Gallery.Develop;

/// <summary>
/// Crop and straighten (<see cref="CropSettings"/>), done before any other adjustment. Steps:
/// 1. Fit the crop rectangle inside the turned picture (move its centre toward the picture's centre, then shrink it
///    around its own centre), so no corner is left empty. The editor already keeps it inside while dragging; this is a
///    safety net.
/// 2. Turn the picture around its centre and cut the rectangle, level with the screen. Each output pixel reads the
///    matching point of the upright picture, smoothed in linear light (Catmull-Rom).
/// With no turn, the rectangle snaps to whole pixels and is copied as it is.
/// </summary>
public static class Geometry
{
    /// <summary>The crop rectangle in pixels of a w × h picture, in the turned frame: its top left, width and height.</summary>
    public static (double X, double Y, double W, double H) Box(CropSettings crop, double w, double h)
    {
        if (crop.Angle == 0)
        {
            double x0 = Math.Min(Math.Round(crop.Left * w), w - 1), y0 = Math.Min(Math.Round(crop.Top * h), h - 1);
            double x1 = Math.Clamp(Math.Round(crop.Right * w), x0 + 1, w), y1 = Math.Clamp(Math.Round(crop.Bottom * h), y0 + 1, h);
            return (x0, y0, x1 - x0, y1 - y0);
        }
        return Fit(crop.Left * w, crop.Top * h, (crop.Right - crop.Left) * w, (crop.Bottom - crop.Top) * h, crop.Angle, w, h);
    }

    /// <summary>The cropped picture's size, for a w × h whole picture.</summary>
    public static (int W, int H) Size(CropSettings crop, int w, int h)
    {
        if (crop.IsIdentity) return (w, h);
        var (_, _, bw, bh) = Box(crop, w, h);
        return (Math.Max(1, (int)Math.Round(bw)), Math.Max(1, (int)Math.Round(bh)));
    }

    /// <summary>
    /// Moves the rectangle (x, y, bw, bh) toward the picture's centre and shrinks it around its own centre until all its
    /// corners are inside the w × h picture turned by angle. The desktop editor also uses this to keep its crop frame inside.
    /// </summary>
    public static (double X, double Y, double W, double H) Fit(double x, double y, double bw, double bh, double angle, double w, double h)
    {
        var rad = angle * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad), hw = w / 2, hh = h / 2;
        // Turn a point back into the picture's own axes, measured from its centre.
        (double X, double Y) Back(double dx, double dy) => (cos * dx + sin * dy, -sin * dx + cos * dy);
        double mx = x + bw / 2 - hw, my = y + bh / 2 - hh;
        var (cx, cy) = Back(mx, my);
        var inside = Math.Min(Math.Abs(cx) > hw ? hw / Math.Abs(cx) : 1, Math.Abs(cy) > hh ? hh / Math.Abs(cy) : 1);
        (mx, my, cx, cy) = (mx * inside, my * inside, cx * inside, cy * inside);
        // Each corner, centre + t × (±bw/2, ±bh/2), must keep both coordinates within half the picture's sides.
        // Find the largest t.
        var t = 1.0;
        foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            var (ux, uy) = Back(sx * bw / 2, sy * bh / 2);
            if (ux > 0) t = Math.Min(t, (hw - cx) / ux); else if (ux < 0) t = Math.Min(t, (hw + cx) / -ux);
            if (uy > 0) t = Math.Min(t, (hh - cy) / uy); else if (uy < 0) t = Math.Min(t, (hh + cy) / -uy);
        }
        t = Math.Max(0, t);
        double fw = bw * t, fh = bh * t;
        return (hw + mx - fw / 2, hh + my - fh / 2, fw, fh);
    }

    /// <summary>
    /// Makes the cropped, straightened picture from <paramref name="source"/> (the whole picture, at any size). The output
    /// has one pixel per source pixel, the same way <see cref="ApplyRegion"/> cuts the crops for the 100% view.
    /// </summary>
    public static LinearImage Apply(LinearImage source, CropSettings crop, int threads = 1)
    {
        if (crop.IsIdentity) return source;
        var (w, h) = Size(crop, source.Width, source.Height);
        return Apply(source, crop, w, h, 1, 1, threads);
    }

    /// <summary>Like <see cref="Apply(LinearImage,CropSettings,int)"/>, but scaled to <paramref name="width"/> × <paramref name="height"/> (for an export decoded a little larger than it needs).</summary>
    public static LinearImage Apply(LinearImage source, CropSettings crop, int width, int height, int threads = 1)
    {
        var (_, _, bw, bh) = Box(crop, source.Width, source.Height);
        return Apply(source, crop, width, height, bw / width, bh / height, threads);
    }

    private static LinearImage Apply(LinearImage source, CropSettings crop, int width, int height, double kx, double ky, int threads)
    {
        if (source.Header.CropX is not null) throw new ArgumentException("the source must be the whole picture");
        var (bx, by, _, _) = Box(crop, source.Width, source.Height);
        var map = Map(crop.Angle, source.Width / 2.0, source.Height / 2.0, bx, by, kx, ky, 0, 0, 0, 0);
        var header = source.Header;
        int fullW = header.FullWidth > 0 ? header.FullWidth : source.Width, fullH = header.FullHeight > 0 ? header.FullHeight : source.Height;
        var (cw, ch) = Size(crop, fullW, fullH);
        header = header with
        {
            Width = width, Height = height, FullWidth = cw, FullHeight = ch,
            UncroppedWidth = header.UncroppedWidth ?? fullW, UncroppedHeight = header.UncroppedHeight ?? fullH,
        };
        return Resample(source, header, width, height, map, threads);
    }

    /// <summary>
    /// Finds where the region (x, y, w, h) of the cropped picture (in full-resolution pixels) comes from in the whole
    /// full-resolution picture. Returns the rectangle to read, with extra room for smoothing, kept inside the picture.
    /// </summary>
    public static (int X, int Y, int W, int H) SourceRegion(CropSettings crop, int fullW, int fullH, int x, int y, int w, int h)
    {
        var (bx, by, _, _) = Box(crop, fullW, fullH);
        var rad = crop.Angle * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad), hw = fullW / 2.0, hh = fullH / 2.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in new[] { (x, y), (x + w, y), (x, y + h), (x + w, y + h) })
        {
            double dx = bx + px - hw, dy = by + py - hh;
            double sx = hw + cos * dx + sin * dy, sy = hh - sin * dx + cos * dy;
            (minX, minY, maxX, maxY) = (Math.Min(minX, sx), Math.Min(minY, sy), Math.Max(maxX, sx), Math.Max(maxY, sy));
        }
        int x0 = Math.Clamp((int)Math.Floor(minX) - 3, 0, fullW - 1), y0 = Math.Clamp((int)Math.Floor(minY) - 3, 0, fullH - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(maxX) + 3, x0 + 1, fullW), y1 = Math.Clamp((int)Math.Ceiling(maxY) + 3, y0 + 1, fullH);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Makes the region (x, y, w, h) of the cropped picture at full resolution from <paramref name="part"/>, the part of
    /// the whole full-resolution picture that <see cref="SourceRegion"/> chose (its CropX and CropY say where it is).
    /// </summary>
    public static LinearImage ApplyRegion(LinearImage part, CropSettings crop, int x, int y, int w, int h, int threads = 1)
    {
        var header = part.Header;
        int fullW = header.FullWidth, fullH = header.FullHeight;
        var (bx, by, _, _) = Box(crop, fullW, fullH);
        var (cw, ch) = Size(crop, fullW, fullH);
        var map = Map(crop.Angle, fullW / 2.0, fullH / 2.0, bx, by, 1, 1, x, y, header.CropX ?? 0, header.CropY ?? 0);
        header = header with
        {
            Width = w, Height = h, CropX = x, CropY = y, FullWidth = cw, FullHeight = ch,
            UncroppedWidth = header.UncroppedWidth ?? fullW, UncroppedHeight = header.UncroppedHeight ?? fullH,
        };
        return Resample(part, header, w, h, map, threads);
    }

    /// <summary>
    /// Output pixel (i, j) reads the source at (X0 + i·Ux + j·Vx, Y0 + i·Uy + j·Vy), in source pixel indices (pixel
    /// centres are at whole numbers).
    /// </summary>
    private readonly record struct Affine(double X0, double Y0, double Ux, double Uy, double Vx, double Vy);

    /// <summary>
    /// Builds the map for: a picture turned by angle around (cx, cy), cut at (bx, by), with kx × ky turned-frame pixels per
    /// output pixel, output pixels counted from (rx, ry), and a source whose first pixel is at (ox, oy) of the picture.
    /// </summary>
    private static Affine Map(double angle, double cx, double cy, double bx, double by, double kx, double ky, double rx, double ry, double ox, double oy)
    {
        var rad = angle * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        // Find the first output pixel's centre in the turned frame (from the centre), then turn it back into the picture.
        double dx = bx + (rx + 0.5) * kx - cx, dy = by + (ry + 0.5) * ky - cy;
        return new Affine(cx + cos * dx + sin * dy - 0.5 - ox, cy - sin * dx + cos * dy - 0.5 - oy, cos * kx, -sin * kx, sin * ky, cos * ky);
    }

    private static unsafe LinearImage Resample(LinearImage src, DevelopHeader header, int ow, int oh, Affine m, int threads)
    {
        int sw = src.Width, sh = src.Height;
        float[] r = new float[ow * oh], g = new float[ow * oh], b = new float[ow * oh];
        var copy = m is { Ux: 1, Uy: 0, Vx: 0, Vy: 1 } && m.X0 == Math.Floor(m.X0) && m.Y0 == Math.Floor(m.Y0)
            && m.X0 >= 0 && m.Y0 >= 0 && m.X0 + ow <= sw && m.Y0 + oh <= sh;
        void Row(int j)
        {
            var o = j * ow;
            if (copy)
            {
                var s = ((int)m.Y0 + j) * sw + (int)m.X0;
                Array.Copy(src.R, s, r, o, ow); Array.Copy(src.G, s, g, o, ow); Array.Copy(src.B, s, b, o, ow);
                return;
            }
            fixed (float* pr = src.R, pg = src.G, pb = src.B, qr = r, qg = g, qb = b)
            {
                double x = m.X0 + j * m.Vx, y = m.Y0 + j * m.Vy;
                for (var i = 0; i < ow; i++, x += m.Ux, y += m.Uy)
                {
                    int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                    float tx = (float)(x - x0), ty = (float)(y - y0);
                    // Catmull-Rom weights for the four columns and rows around the point; edge pixels repeat outside.
                    float tx2 = tx * tx, tx3 = tx2 * tx, ty2 = ty * ty, ty3 = ty2 * ty;
                    float a0 = 0.5f * (-tx3 + 2 * tx2 - tx), a1 = 0.5f * (3 * tx3 - 5 * tx2 + 2), a2 = 0.5f * (-3 * tx3 + 4 * tx2 + tx), a3 = 0.5f * (tx3 - tx2);
                    float b0 = 0.5f * (-ty3 + 2 * ty2 - ty), b1 = 0.5f * (3 * ty3 - 5 * ty2 + 2), b2 = 0.5f * (-3 * ty3 + 4 * ty2 + ty), b3 = 0.5f * (ty3 - ty2);
                    int c0 = Clamp(x0 - 1, sw), c1 = Clamp(x0, sw), c2 = Clamp(x0 + 1, sw), c3 = Clamp(x0 + 2, sw);
                    float sr = 0, sg = 0, sb = 0;
                    for (var k = 0; k < 4; k++)
                    {
                        var row = Clamp(y0 - 1 + k, sh) * sw;
                        var wy = k == 0 ? b0 : k == 1 ? b1 : k == 2 ? b2 : b3;
                        sr += wy * (a0 * pr[row + c0] + a1 * pr[row + c1] + a2 * pr[row + c2] + a3 * pr[row + c3]);
                        sg += wy * (a0 * pg[row + c0] + a1 * pg[row + c1] + a2 * pg[row + c2] + a3 * pg[row + c3]);
                        sb += wy * (a0 * pb[row + c0] + a1 * pb[row + c1] + a2 * pb[row + c2] + a3 * pb[row + c3]);
                    }
                    qr[o + i] = sr; qg[o + i] = sg; qb[o + i] = sb;
                }
            }
        }
        if (threads > 1) Parallel.For(0, oh, new ParallelOptions { MaxDegreeOfParallelism = threads }, Row);
        else for (var j = 0; j < oh; j++) Row(j);
        return new LinearImage(header, ow, oh, r, g, b);
    }

    private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
}
