using Gallery.Develop;

namespace Gallery.Desktop.ViewModels;

/// <summary>Which edges of the crop frame a drag moves: one side, or two for a corner. None moves the whole frame.</summary>
[Flags]
public enum CropHandle
{
    Move = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
}

/// <summary>A crop frame in picture pixels, in the turned picture: its top left, width and height.</summary>
public readonly record struct CropBox(double X, double Y, double W, double H)
{
    public static CropBox Lerp(CropBox a, CropBox b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.W + (b.W - a.W) * t, a.H + (b.H - a.H) * t);
}

/// <summary>
/// Crop frame math, the same as the web editor's (wwwroot/editor.js, "crop and straighten"), for a picture of
/// <see cref="Width"/> × <see cref="Height"/> pixels (upright, as edited, at full size). The frame is a rectangle level
/// with the screen, on the picture turned around its centre by the crop's angle, as Gallery.Develop's Geometry cuts it.
/// The settings store it as fractions of the width and height. Every move keeps it inside the turned picture.
/// </summary>
public sealed class CropFrame(double width, double height)
{
    public double Width { get; } = width;
    public double Height { get; } = height;

    /// <summary>The frame for these crop settings.</summary>
    public CropBox Of(CropSettings c) => new(c.Left * Width, c.Top * Height, (c.Right - c.Left) * Width, (c.Bottom - c.Top) * Height);

    /// <summary>The crop settings updated to this frame.</summary>
    public CropSettings With(CropSettings c, CropBox b)
    {
        static double F(double v) => Math.Clamp(Math.Round(v, 6), 0, 1);
        return c with { Left = F(b.X / Width), Top = F(b.Y / Height), Right = F((b.X + b.W) / Width), Bottom = F((b.Y + b.H) / Height) };
    }

    /// <summary>True when all the frame's corners, turned back by the angle into the picture's own axes, are inside the picture.</summary>
    public bool Inside(CropBox b, double angle)
    {
        var r = angle * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r), hw = Width / 2, hh = Height / 2;
        foreach (var (px, py) in new[] { (b.X, b.Y), (b.X + b.W, b.Y), (b.X, b.Y + b.H), (b.X + b.W, b.Y + b.H) })
        {
            double dx = px - hw, dy = py - hh;
            if (Math.Abs(cos * dx + sin * dy) > hw + 0.01 || Math.Abs(-sin * dx + cos * dy) > hh + 0.01) return false;
        }
        return true;
    }

    /// <summary>Moves the frame toward the picture's centre and shrinks it around its own centre until it is inside the turned picture.</summary>
    public CropBox Fit(CropBox b, double angle)
    {
        var (x, y, w, h) = Geometry.Fit(b.X, b.Y, b.W, b.H, angle, Width, Height);
        return new CropBox(x, y, w, h);
    }

    /// <summary>Moves the frame from <paramref name="from"/> toward <paramref name="to"/>, as far as it can go and stay inside the turned picture.</summary>
    public CropBox Toward(CropBox from, CropBox to, double angle)
    {
        if (Inside(to, angle)) return to;
        double lo = 0, hi = 1;
        for (var i = 0; i < 24; i++)
        {
            var t = (lo + hi) / 2;
            if (Inside(CropBox.Lerp(from, to, t), angle)) lo = t; else hi = t;
        }
        return CropBox.Lerp(from, to, lo);
    }

    /// <summary>The width ÷ height the frame must keep, for the way the frame is turned now. Null when free.</summary>
    public double? LockedRatio(CropSettings c)
    {
        var r = c.Aspect == "original" ? Width / Height : CropSettings.Ratio(c.Aspect);
        if (r is not { } ratio) return null;
        if (ratio < 1) ratio = 1 / ratio;
        var b = Of(c);
        return b.W >= b.H ? ratio : 1 / ratio;
    }

    /// <summary>Drags the frame by (dx, dy) picture pixels. It slides along the picture's edges.</summary>
    public CropBox Moved(CropBox s, double dx, double dy, double angle)
    {
        var b = Toward(s, s with { X = s.X + dx, Y = s.Y + dy }, angle);
        b = Toward(b, b with { X = s.X + dx }, angle);
        return Toward(b, b with { Y = s.Y + dy }, angle);
    }

    /// <summary>
    /// Drags the edges of <paramref name="handle"/> by (dx, dy) picture pixels. Keeps <paramref name="ratio"/>
    /// (width ÷ height) when there is one. The frame stays at least 2% of a side.
    /// </summary>
    public CropBox Resized(CropBox s, CropHandle handle, double dx, double dy, double angle, double? ratio)
    {
        double mw = 0.02 * Width, mh = 0.02 * Height;
        double l = s.X, t = s.Y, r = s.X + s.W, b = s.Y + s.H;
        if (handle.HasFlag(CropHandle.Left)) l = Math.Min(l + dx, r - mw);
        if (handle.HasFlag(CropHandle.Right)) r = Math.Max(r + dx, l + mw);
        if (handle.HasFlag(CropHandle.Top)) t = Math.Min(t + dy, b - mh);
        if (handle.HasFlag(CropHandle.Bottom)) b = Math.Max(b + dy, t + mh);
        if (ratio is not { } k)
        {
            // First across, then up and down. Each one stops at the picture's edge by itself.
            var across = Toward(s, s with { X = l, W = r - l }, angle);
            return Toward(across, across with { Y = t, H = b - t }, angle);
        }
        double w = r - l, h = b - t;
        CropBox box;
        var sideways = handle & (CropHandle.Left | CropHandle.Right);
        var upright = handle & (CropHandle.Top | CropHandle.Bottom);
        if (sideways != 0 && upright != 0)
        {
            // A corner: the side that moved more sets the size. The opposite corner stays.
            var f = Math.Abs(w / s.W - 1) >= Math.Abs(h / s.H - 1) ? w / s.W : h / s.H;
            w = Math.Max(Math.Max(mw, mh * k), s.W * f);
            h = w / k;
            box = new CropBox(handle.HasFlag(CropHandle.Left) ? s.X + s.W - w : s.X, handle.HasFlag(CropHandle.Top) ? s.Y + s.H - h : s.Y, w, h);
        }
        else if (sideways != 0)
        {
            h = w / k;
            box = new CropBox(l, s.Y + (s.H - h) / 2, w, h);
        }
        else
        {
            w = h * k;
            box = new CropBox(s.X + (s.W - w) / 2, t, w, h);
        }
        return Toward(s, box, angle);
    }
}
