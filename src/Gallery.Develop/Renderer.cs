namespace Gallery.Develop;

/// <summary>
/// Renders an edit: <see cref="LinearImage"/> + <see cref="DevelopSettings"/> → 8-bit sRGB RGBA. Each pixel takes only
/// three steps, so it stays fast in WebAssembly (see docs/raw-editor.md, Phase 0):
/// <list type="number">
/// <item>One 3×3 matrix: white balance (Bradford adaptation in Rec.2020), exposure, and conversion to linear sRGB.</item>
/// <item>Saturation and vibrance, keeping luminance (saturation gives the same result in either space, because both share
/// luminance and white). Then colours sRGB cannot show are moved toward their own luminance, keeping their hue and
/// brightness.</item>
/// <item>One table per channel for everything that works on a single value: contrast, the base tone curve (RAWs), sRGB
/// encoding, brightness, the RGB curve and the channel's curve. The index is the float's bits (exponent and top mantissa
/// bits), so a lookup is just a shift and a subtract.</item>
/// </list>
/// When highlights, shadows or noise reduction are on, the picture is done in overlapping tiles:
/// matrix -> colour and luminance noise reduction (guided filters that follow the log brightness) -> highlights and
/// shadows (see <see cref="LocalTone"/>) -> the steps above -> output sharpening. Filter sizes are in pixels of a
/// 24-megapixel picture, scaled to the rendered size, so a preview, a 100% crop and an export look the same. The
/// histogram samples every 4th pixel of large images. Outside the browser, threads can share the work
/// (<see cref="Parallelism"/>).
/// </summary>
public sealed partial class Renderer
{
    private const int MinExponent = -16, MaxExponent = 5, MantissaBits = 10;
    /// <summary>Entries per channel table: every float from 2^-16 to 2^5 at 1/1024 steps of its exponent.</summary>
    internal const int TableSize = (MaxExponent - MinExponent) << MantissaBits;
    private const int IndexOffset = (127 + MinExponent) << MantissaBits;

    private readonly byte[] _red = new byte[TableSize], _green = new byte[TableSize], _blue = new byte[TableSize];
    private readonly float[] _matrix = new float[9];
    private float _saturation = 1, _vibrance;
    private bool _colour;
    private DevelopSettings? _prepared;
    private DevelopSettings? _clamped;
    private bool _preparedRaw;

    /// <summary>log2 of each table entry's value, so log brightness is a lookup, as fast as the tone tables.</summary>
    private static readonly float[] Log2Table = Enumerable.Range(0, TableSize).Select(i => (float)Math.Log2(ValueOf(i))).ToArray();

    /// <summary>The scene value a table entry stands for (the middle of its range).</summary>
    internal static double ValueOf(int index)
    {
        var e = MinExponent + (index >> MantissaBits);
        var m = (index & ((1 << MantissaBits) - 1)) + 0.5;
        return Math.Pow(2, e) * (1 + m / (1 << MantissaBits));
    }

    /// <summary>Builds the matrix and tables for these settings (skipped when they haven't changed).</summary>
    public void Prepare(DevelopSettings settings, bool raw)
    {
        if (ReferenceEquals(settings, _prepared) && raw == _preparedRaw) return;
        var s = _clamped = settings.Clamped();
        var wb = ColorMath.WhiteBalance(s.WhiteBalance.Temperature, s.WhiteBalance.Tint);
        var gain = Math.Pow(2, s.Exposure);
        for (var i = 0; i < 9; i++) _matrix[i] = (float)(wb[i] * gain);
        _saturation = (float)(1 + s.Saturation / 100);
        _vibrance = (float)(s.Vibrance / 100);
        _colour = s.Saturation != 0 || s.Vibrance != 0;
        var m = ColorMath.Multiply(ColorMath.ToDouble(ColorMath.Rec2020ToSrgb), ColorMath.ToDouble(_matrix));
        for (var i = 0; i < 9; i++) _matrix[i] = (float)m[i];
        var rgb = ToneCurve.Spline(s.Curves.Rgb);
        var curves = new[] { ToneCurve.Spline(s.Curves.Red), ToneCurve.Spline(s.Curves.Green), ToneCurve.Spline(s.Curves.Blue) };
        var tables = new[] { _red, _green, _blue };
        var slope = ToneCurve.SlopeFor(s.Contrast);
        for (var i = 0; i < TableSize; i++)
        {
            var x = ValueOf(i);
            double y;
            if (raw) y = ColorMath.LinearToSrgb((float)ToneCurve.Filmic(x, slope));
            else y = ToneCurve.SCurve(ColorMath.LinearToSrgb((float)Math.Min(1, x)), s.Contrast);
            y = ToneCurve.Brighten(y, s.Brightness);
            y = ToneCurve.Lookup(rgb, y);
            for (var c = 0; c < 3; c++)
                tables[c][i] = (byte)Math.Clamp(Math.Round(ToneCurve.Lookup(curves[c], y) * 255), 0, 255);
        }
        _prepared = settings;
        _preparedRaw = raw;
    }

    /// <summary>Tile size (pixels per side) for the spatial adjustments. Internal so tests can check that tiles agree.</summary>
    internal int TileSize { get; set; } = 512;

    /// <summary>
    /// Number of threads to render with. Tiles (or bands of rows, without spatial adjustments) are shared out
    /// between them. The picture and histogram come out the same for any count. Use 1 in the browser, which has one thread.
    /// </summary>
    public int Parallelism { get; set; } = 1;

    private const int BandRows = 64;

    /// <summary>What one thread needs to render: scratch arrays, a histogram count and its own reader of the tone grid.</summary>
    private sealed class Worker
    {
        public readonly Scratch Scratch = new();
        public readonly Histogram Histogram = new();
        public int Sampled, Highlights, Shadows;
        private LocalTone.Sampler? _tone;

        public LocalTone.Sampler SamplerFor(LocalTone tone) => _tone?.Tone == tone ? _tone : _tone = new LocalTone.Sampler(tone);

        public void Reset()
        {
            Histogram.Clear();
            Sampled = Highlights = Shadows = 0;
        }
    }

    private readonly List<Worker> _workers = [new()];
    private LinearImage? _contextSource, _contextSmall;
    private LocalTone? _tone;
    private DevelopSettings? _toneFor;

    /// <summary>
    /// Renders into <paramref name="rgba"/> (Width × Height × 4 bytes) and fills <paramref name="histogram"/> when given.
    /// <paramref name="context"/> is the whole picture when <paramref name="image"/> is only part of it (a 100% crop).
    /// Highlights and shadows are then measured on the whole picture, as in the preview and the export.
    /// </summary>
    public unsafe void Render(LinearImage image, DevelopSettings settings, Span<byte> rgba, Histogram? histogram = null, LinearImage? context = null)
    {
        var n = image.Width * image.Height;
        if (rgba.Length < n * 4) throw new ArgumentException("the output is too small");
        Prepare(settings, image.IsRaw);
        var s = _clamped!;
        var plan = new Plan(image, s);
        var fast = !plan.Spatial && !plan.Sharpen;
        LocalTone? tone = null;
        if (!fast && (s.Highlights != 0 || s.Shadows != 0))
        {
            var whole = context ?? image;
            if (!ReferenceEquals(whole, _contextSource)) { _contextSmall = LocalTone.Reduce(whole); _contextSource = whole; _tone = null; }
            if (_tone is null || !ReferenceEquals(_toneFor, settings)) { _tone = LocalTone.Build(_contextSmall!, _matrix, image.IsRaw, s.Shadows, s.Highlights, _workers[0].Scratch); _toneFor = settings; }
            tone = _tone;
        }
        // Split the work: bands of rows when each pixel is independent, otherwise tiles (each with a margin for the filters).
        var tile = Math.Max(64, TileSize);
        var columns = (image.Width + tile - 1) / tile;
        var jobs = fast ? (image.Height + BandRows - 1) / BandRows : columns * ((image.Height + tile - 1) / tile);
        var threads = Math.Clamp(Parallelism, 1, Math.Max(1, jobs));
        while (_workers.Count < threads) _workers.Add(new Worker());
        for (var t = 0; t < threads; t++) _workers[t].Reset();
        var count = histogram is not null;
        fixed (byte* output = rgba)
        {
            var address = (nint)output;
            var length = rgba.Length;
            void Run(int job, Worker wk)
            {
                var span = new Span<byte>((void*)address, length);
                if (fast)
                {
                    var y0 = job * BandRows;
                    Shade(image.R, image.G, image.B, image.Width, image.Height, 0, y0, image.Width, Math.Min(BandRows, image.Height - y0), _matrix, image.IsRaw,
                        span, image.Width, 0, y0, count ? wk : null);
                    return;
                }
                int tx = job % columns * tile, ty = job / columns * tile;
                RenderTile(image, tx, ty, Math.Min(tile, image.Width - tx), Math.Min(tile, image.Height - ty), plan, tone, span, wk, count);
            }
            if (threads == 1)
                for (var j = 0; j < jobs; j++) Run(j, _workers[0]);
            else
            {
                var next = -1;
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    var wk = _workers[t];
                    for (int j; (j = Interlocked.Increment(ref next)) < jobs;) Run(j, wk);
                });
            }
        }
        if (histogram is not null) Merge(histogram, threads);
    }

    /// <summary>Adds the counts from all threads into the histogram.</summary>
    private void Merge(Histogram h, int threads)
    {
        h.Clear();
        for (var t = 0; t < threads; t++)
        {
            var wk = _workers[t];
            var w = wk.Histogram;
            for (var i = 0; i < 256; i++)
            {
                h.Red[i] += w.Red[i];
                h.Green[i] += w.Green[i];
                h.Blue[i] += w.Blue[i];
                h.Luma[i] += w.Luma[i];
            }
            h.Pixels += wk.Sampled;
            h.ClippedHighlights += wk.Highlights;
            h.ClippedShadows += wk.Shadows;
        }
    }

    /// <summary>Radii and switches for the spatial adjustments, in pixels of the picture being rendered.</summary>
    private readonly struct Plan
    {
        public readonly bool Spatial, Sharpen;
        public readonly int ChromaRadius, LumaRadius, Margin;
        public readonly float ChromaAmount, LumaAmount, ChromaEps, LumaEps, Sigma, SharpenAmount, Threshold;
        public readonly float U0, V0, Du, Dv;

        public Plan(LinearImage image, DevelopSettings s)
        {
            var h = image.Header;
            int fullW = h.FullWidth > 0 ? h.FullWidth : image.Width, fullH = h.FullHeight > 0 ? h.FullHeight : image.Height;
            // Rendered pixels per full-size pixel: 1 for a crop, less for a smaller copy.
            var f = h.CropX is not null ? 1.0 : (double)image.Width / fullW;
            // Size of one pixel of a 24-megapixel picture at this scale, measured on the whole picture, cropped or not.
            var unit = Math.Sqrt((double)(h.UncroppedWidth ?? fullW) * (h.UncroppedHeight ?? fullH) / 24e6) * f;
            ChromaAmount = (float)(s.NoiseReduction.Color / 100);
            LumaAmount = (float)(s.NoiseReduction.Luminance / 100);
            ChromaRadius = ChromaAmount > 0 ? Math.Max(1, (int)Math.Round((2 + 8 * ChromaAmount) * unit)) : 0;
            LumaRadius = LumaAmount > 0 ? Math.Max(1, (int)Math.Round((1 + 2 * LumaAmount) * unit)) : 0;
            ChromaEps = 0.15f * 0.15f;
            LumaEps = MathF.Pow(0.02f + 0.25f * LumaAmount, 2);
            Sharpen = s.Sharpening.Amount > 0;
            Sigma = (float)Math.Max(0.4, s.Sharpening.Radius * unit);
            SharpenAmount = (float)(s.Sharpening.Amount / 100);
            Threshold = (float)(s.Sharpening.Masking / 100 * 12);
            Spatial = ChromaRadius > 0 || LumaRadius > 0 || s.Highlights != 0 || s.Shadows != 0;
            Margin = 2 * Math.Max(ChromaRadius, LumaRadius) + (Sharpen ? (int)Math.Ceiling(3 * Sigma) + 1 : 0) + 2;
            // Position of this part in the whole picture, for the highlights-and-shadows grid.
            U0 = h.CropX is { } cx ? (float)cx / fullW : 0;
            V0 = h.CropY is { } cy ? (float)cy / fullH : 0;
            Du = h.CropX is not null ? 1f / fullW : 1f / image.Width;
            Dv = h.CropY is not null ? 1f / fullH : 1f / image.Height;
        }
    }

    /// <summary>One tile, with a margin for the filters; only its inner part is written to the output.</summary>
    private void RenderTile(LinearImage image, int x0, int y0, int tw, int th, Plan p, LocalTone? tone, Span<byte> rgba, Worker wk, bool count)
    {
        int ex0 = Math.Max(0, x0 - p.Margin), ey0 = Math.Max(0, y0 - p.Margin);
        int ex1 = Math.Min(image.Width, x0 + tw + p.Margin), ey1 = Math.Min(image.Height, y0 + th + p.Margin);
        int w = ex1 - ex0, h = ey1 - ey0, n = w * h;
        var scratch = wk.Scratch;
        scratch.Ensure(n, w);
        var f = scratch.F;
        var tileRgba = scratch.Bytes;
        var tally = count ? wk : null;
        if (!p.Spatial)
        {
            // Only sharpening is on: colour the tile straight from the source, then sharpen.
            Shade(image.R, image.G, image.B, image.Width, image.Height, ex0, ey0, w, h, _matrix, image.IsRaw, tileRgba, w, 0, 0, null);
            SharpenTile(tileRgba, w, h, p, scratch);
            CopyInner(tileRgba, w, x0 - ex0, y0 - ey0, image.Width, x0, y0, tw, th, rgba, tally);
            return;
        }
        float[] r = f[6], g = f[7], b = f[8], lum = f[9], log = f[10];
        // 1. White balance, exposure, convert to linear sRGB (and move rebuilt RAW highlights toward neutral).
        var m = _matrix;
        var needLog = p.Spatial;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var si = (ey0 + y) * image.Width + ex0 + x;
                var i = y * w + x;
                float sx = image.R[si], sy = image.G[si], sz = image.B[si];
                float rr = m[0] * sx + m[1] * sy + m[2] * sz, gg = m[3] * sx + m[4] * sy + m[5] * sz, bb = m[6] * sx + m[7] * sy + m[8] * sz;
                if (image.IsRaw) Neutralise(sx, sy, sz, ref rr, ref gg, ref bb);
                r[i] = rr; g[i] = gg; b[i] = bb;
                if (!needLog) continue;
                var l = ColorMath.SrgbR * rr + ColorMath.SrgbG * gg + ColorMath.SrgbB * bb;
                lum[i] = l;
                log[i] = Log2Table[Index(l)];
            }
        // 2. Colour noise: smooth the colour differences, following the brightness edges. Colour noise is coarse and the
        //    eye sees colour at low detail, so this runs at half size and is scaled back up.
        if (p.ChromaRadius > 0)
        {
            float[] cr = f[11], cb = f[12], qr = f[13], qb = f[14], half = f[15];
            for (var i = 0; i < n; i++) { cr[i] = r[i] - lum[i]; cb[i] = b[i] - lum[i]; }
            var small = w >= 8 && h >= 8 && p.ChromaRadius >= 2;
            int hw = w, hh = h;
            float[] gl = log, gr = cr, gb = cb;
            if (small)
            {
                // Shrink the guide and both colour differences by half into the spare arrays (qr, qb, half), and filter there.
                Filters.Halve(log, w, h, half, out hw, out hh);
                Filters.Halve(cr, w, h, qr, out _, out _);
                Filters.Halve(cb, w, h, qb, out _, out _);
                Array.Copy(qr, cr, hw * hh); Array.Copy(qb, cb, hw * hh);
                (gl, gr, gb) = (half, cr, cb);
            }
            Filters.Guided(gl, [gr, gb], [qr, qb], hw, hh, small ? Math.Max(1, p.ChromaRadius / 2) : p.ChromaRadius, p.ChromaEps, scratch);
            if (small)
            {
                // Scale the smoothed differences back to full size into cr and cb (the half-size inputs are not needed after this).
                Filters.Double(qr, hw, hh, cr, w, h);
                Filters.Double(qb, hw, hh, cb, w, h);
            }
            else { Array.Copy(qr, cr, n); Array.Copy(qb, cb, n); }
            var a = p.ChromaAmount;
            for (var i = 0; i < n; i++)
            {
                float l = lum[i], or = r[i] - l, ob = b[i] - l;
                var nr = l + or + a * (cr[i] - or);
                var nb = l + ob + a * (cb[i] - ob);
                r[i] = nr; b[i] = nb;
                g[i] = (l - ColorMath.SrgbR * nr - ColorMath.SrgbB * nb) / ColorMath.SrgbG;
            }
        }
        // 3. Luminance noise: smooth the log brightness but keep the edges.
        if (p.LumaRadius > 0)
        {
            var q = f[11];
            Filters.Guided(log, [log], [q], w, h, p.LumaRadius, p.LumaEps, scratch);
            for (var i = 0; i < n; i++)
            {
                var d = p.LumaAmount * (q[i] - log[i]);
                var k = Filters.Exp2Small(d);
                r[i] *= k; g[i] *= k; b[i] *= k;
                log[i] += d;
            }
        }
        // 4. Highlights and shadows from the whole picture's base layer.
        if (tone is not null)
        {
            var gain = f[11];
            var sampler = wk.SamplerFor(tone);
            sampler.Columns(p.U0 + (ex0 + 0.5f) * p.Du, p.Du, w);
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                sampler.Row(log.AsSpan(row, w), p.V0 + (ey0 + y + 0.5f) * p.Dv, gain.AsSpan(0, w));
                for (var x = 0; x < w; x++) { var k = gain[x]; r[row + x] *= k; g[row + x] *= k; b[row + x] *= k; }
            }
        }
        // 5. Colour, gamut and tone tables into the tile's own RGBA -> sharpen -> copy the inner part to the output.
        Shade(r, g, b, w, h, 0, 0, w, h, Identity, false, tileRgba, w, 0, 0, null);
        if (p.Sharpen) SharpenTile(tileRgba, w, h, p, scratch);
        CopyInner(tileRgba, w, x0 - ex0, y0 - ey0, image.Width, x0, y0, tw, th, rgba, tally);
    }

    private static void CopyInner(byte[] tile, int tileWidth, int ix, int iy, int width, int x0, int y0, int tw, int th, Span<byte> rgba, Worker? tally)
    {
        for (var y = 0; y < th; y++)
            tile.AsSpan(((iy + y) * tileWidth + ix) * 4, tw * 4).CopyTo(rgba.Slice(((y0 + y) * width + x0) * 4, tw * 4));
        if (tally is not null) Count(rgba, width, x0, y0, tw, th, tally);
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>Rebuilt RAW highlights (above sensor white) have unreliable colour, so move them toward neutral.</summary>
    private static void Neutralise(float sx, float sy, float sz, ref float r, ref float g, ref float b)
    {
        var top = MathF.Max(sx, MathF.Max(sy, sz));
        if (top <= 1f) return;
        var t = MathF.Min(1f, top - 1f) * 0.8f;
        var l = ColorMath.SrgbR * r + ColorMath.SrgbG * g + ColorMath.SrgbB * b;
        r += (l - r) * t; g += (l - g) * t; b += (l - b) * t;
    }

    /// <summary>
    /// Output sharpening on the brightness of the rendered tile (unsharp mask). Flat areas are left alone: only
    /// differences larger than the threshold (in 8-bit levels) are boosted, fading in over one more threshold's width.
    /// </summary>
    private static void SharpenTile(byte[] rgba, int w, int h, Plan p, Scratch scratch)
    {
        var n = w * h;
        float[] l = scratch.F[11], blur = scratch.F[12], tmp = scratch.F[13];
        for (var i = 0; i < n; i++) l[i] = 0.2126f * rgba[i * 4] + 0.7152f * rgba[i * 4 + 1] + 0.0722f * rgba[i * 4 + 2];
        Filters.Gaussian(l, blur, tmp, w, h, p.Sigma);
        var t = p.Threshold;
        for (var i = 0; i < n; i++)
        {
            var detail = l[i] - blur[i];
            var weight = t <= 0 ? 1f : Math.Clamp((MathF.Abs(detail) - t) / t, 0f, 1f);
            var add = p.SharpenAmount * weight * detail;
            if (add == 0) continue;
            var o = i * 4;
            rgba[o] = (byte)Math.Clamp(rgba[o] + add + 0.5f, 0, 255);
            rgba[o + 1] = (byte)Math.Clamp(rgba[o + 1] + add + 0.5f, 0, 255);
            rgba[o + 2] = (byte)Math.Clamp(rgba[o + 2] + add + 0.5f, 0, 255);
        }
    }

    private static void Count(Span<byte> rgba, int stride, int x0, int y0, int tw, int th, Worker tally)
    {
        var h = tally.Histogram;
        int sampled = 0, highlights = 0, shadows = 0;
        var step = (long)stride * th > 65536 ? 4 : 1;
        for (var y = 0; y < th; y++)
            for (var x = 0; x < tw; x += 1)
            {
                var i = (y0 + y) * stride + x0 + x;
                if (i % step != 0) continue;
                var o = i * 4;
                byte br = rgba[o], bg = rgba[o + 1], bb = rgba[o + 2];
                h.Red[br]++; h.Green[bg]++; h.Blue[bb]++;
                h.Luma[(54 * br + 183 * bg + 19 * bb) >> 8]++;
                sampled++;
                if (br == 255 || bg == 255 || bb == 255) highlights++;
                if (br == 0 || bg == 0 || bb == 0) shadows++;
            }
        tally.Sampled += sampled;
        tally.Highlights += highlights;
        tally.Shadows += shadows;
    }

    /// <summary>
    /// The per-pixel steps for a region: matrix -> (RAW highlight fix) -> saturation and vibrance -> gamut mapping -> tone
    /// tables. Writes at (ox, oy) in an output <paramref name="outStride"/> pixels wide, and counts in the tally when given.
    /// </summary>
    private unsafe void Shade(float[] srcR, float[] srcG, float[] srcB, int srcStride, int srcRows, int x0, int y0, int w, int h,
        float[] matrix, bool neutraliseRaw, Span<byte> rgba, int outStride, int ox, int oy, Worker? tally)
    {
        var histogram = tally?.Histogram;
        int sampled = 0, highlights = 0, shadows = 0;
        float m0 = matrix[0], m1 = matrix[1], m2 = matrix[2], m3 = matrix[3], m4 = matrix[4], m5 = matrix[5], m6 = matrix[6], m7 = matrix[7], m8 = matrix[8];
        float sat = _saturation, vib = _vibrance;
        var colour = _colour;
        var stride = (long)srcStride * srcRows > 65536 ? 4 : 1; // histogram sampling
        fixed (float* pr = srcR, pg = srcG, pb = srcB)
        fixed (byte* tr = _red, tg = _green, tb = _blue, outBytes = rgba)
        fixed (int* hr = histogram?.Red, hg = histogram?.Green, hb = histogram?.Blue, hl = histogram?.Luma)
        {
            var output = (uint*)outBytes;
            for (var y = 0; y < h; y++)
            {
                var si = (y0 + y) * srcStride + x0;
                var oi = (oy + y) * outStride + ox;
                for (var k = 0; k < w; k++, si++, oi++)
                {
                    float x = pr[si], yy = pg[si], z = pb[si];
                    float r = m0 * x + m1 * yy + m2 * z;
                    float g = m3 * x + m4 * yy + m5 * z;
                    float b = m6 * x + m7 * yy + m8 * z;
                    if (neutraliseRaw) Neutralise(x, yy, z, ref r, ref g, ref b);
                    if (colour)
                    {
                        var lum = ColorMath.SrgbR * r + ColorMath.SrgbG * g + ColorMath.SrgbB * b;
                        float dr = r - lum, dg = g - lum, db = b - lum;
                        var f = sat;
                        if (vib != 0)
                        {
                            // How colourful the pixel already is (0 grey .. 1 fully saturated). Strong colours and skin change less.
                            var chroma = MathF.Max(MathF.Abs(dr), MathF.Max(MathF.Abs(dg), MathF.Abs(db))) / (MathF.Abs(lum) + 1e-4f);
                            var weight = 1 - MathF.Min(chroma, 1);
                            if (r > g && g > b && dr > 0) weight *= 0.5f; // reddish-yellow: skin
                            f *= 1 + vib * weight;
                        }
                        if (f < 0) f = 0;
                        r = lum + dr * f; g = lum + dg * f; b = lum + db * f;
                    }
                    var min = MathF.Min(r, MathF.Min(g, b));
                    if (min < 0)
                    {
                        // Outside sRGB: move toward the pixel's own luminance only as far as needed to fit inside.
                        var lum = ColorMath.SrgbR * r + ColorMath.SrgbG * g + ColorMath.SrgbB * b;
                        if (lum <= 0) { r = g = b = 0; }
                        else { var t = lum / (lum - min); r = lum + (r - lum) * t; g = lum + (g - lum) * t; b = lum + (b - lum) * t; }
                    }
                    var br = tr[Index(r)];
                    var bg = tg[Index(g)];
                    var bb = tb[Index(b)];
                    output[oi] = br | (uint)bg << 8 | (uint)bb << 16 | 0xFF000000u;
                    if (hr != null && oi % stride == 0)
                    {
                        sampled++;
                        hr[br]++; hg[bg]++; hb[bb]++;
                        hl[(54 * br + 183 * bg + 19 * bb) >> 8]++;
                        if (br == 255 || bg == 255 || bb == 255) highlights++;
                        if (br == 0 || bg == 0 || bb == 0) shadows++;
                    }
                }
            }
        }
        if (tally is null) return;
        tally.Sampled += sampled;
        tally.Highlights += highlights;
        tally.Shadows += shadows;
    }

    /// <summary>Table index of a value: exponent and top mantissa bits of the float, clamped to the table.</summary>
    private static int Index(float v)
    {
        var i = (BitConverter.SingleToInt32Bits(v) >> (23 - MantissaBits)) - IndexOffset;
        return i < 0 ? 0 : i >= TableSize ? TableSize - 1 : i;
    }
}
