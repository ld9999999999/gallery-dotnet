namespace Gallery.Develop;

/// <summary>
/// Highlights and shadows. A smooth, edge-preserving base layer of the log brightness lifts dark areas and
/// pulls down bright ones, while fine detail (log brightness minus base) stays. The base comes from the whole picture
/// shrunk to 512 px, stored as guided-filter values on a coarse grid (a fast guided filter). The preview, a 100% crop and
/// the export all sample the same grid, so they match, and none needs the whole full-size picture at once. Once built,
/// the grid is read-only, so tiles rendered at the same time can share it, each through its own <see cref="Sampler"/>.
/// </summary>
internal sealed class LocalTone
{
    private const int GridSize = 512;
    private readonly float[] _a, _b;
    private readonly int _w, _h;
    private readonly float _grey, _shadows, _highlights;

    private LocalTone(float[] a, float[] b, int w, int h, float grey, float shadows, float highlights)
    {
        (_a, _b, _w, _h, _grey, _shadows, _highlights) = (a, b, w, h, grey, shadows, highlights);
        _gain = GainTable();
    }

    /// <summary>Builds the grid from the whole picture (any size: it is shrunk first), using the render matrix.</summary>
    public static LocalTone Build(LinearImage small, float[] m, bool raw, double shadows, double highlights, Scratch s)
    {
        int w = small.Width, h = small.Height, n = w * h;
        s.Ensure(n, w);
        var log = new float[n];
        for (var i = 0; i < n; i++)
        {
            float x = small.R[i], y = small.G[i], z = small.B[i];
            var r = m[0] * x + m[1] * y + m[2] * z;
            var g = m[3] * x + m[4] * y + m[5] * z;
            var b = m[6] * x + m[7] * y + m[8] * z;
            log[i] = Filters.FastLog2(MathF.Max(ColorMath.SrgbR * r + ColorMath.SrgbG * g + ColorMath.SrgbB * b, 1e-5f));
        }
        var radius = Math.Max(2, (int)Math.Round(Math.Max(w, h) / 50.0));
        float[] a = new float[n], bb = new float[n];
        Filters.Guided(log, [log], [a, bb], w, h, radius, 0.3f * 0.3f, s, coefficients: true);
        var grey = (float)Math.Log2(raw ? ToneCurve.SceneGrey : 0.18);
        return new LocalTone(a, bb, w, h, grey, (float)(shadows / 100), (float)(highlights / 100));
    }

    /// <summary>The picture shrunk for <see cref="Build"/>.</summary>
    public static LinearImage Reduce(LinearImage whole) => whole.FitWithin(GridSize, GridSize);

    private const float TableLow = -24, TableHigh = 8;
    private const int TableSize = 1024;
    private readonly float[] _gain;

    /// <summary>The brightness factor for a base log brightness: 2^(lift on the dark side + change on the bright side).</summary>
    private float[] GainTable()
    {
        var t = new float[TableSize];
        for (var i = 0; i < TableSize; i++)
        {
            var b = TableLow + (TableHigh - TableLow) * i / (TableSize - 1);
            var lift = _shadows * 2.0f * (1 - Filters.SmoothStep(_grey - 5, _grey + 0.5f, b));
            var compress = _highlights * 1.5f * Filters.SmoothStep(_grey + 0.5f, _grey + 4.5f, b);
            t[i] = MathF.Pow(2, lift + compress);
        }
        return t;
    }

    /// <summary>Reads the grid for one tile at a time. It stores where the tile's columns fall, so each thread needs its own.</summary>
    public sealed class Sampler(LocalTone tone)
    {
        internal int[] X0 = [], X1 = [];
        internal float[] Fx = [], ARow = [], BRow = [];

        public LocalTone Tone { get; } = tone;

        /// <summary>Maps the columns of the next pixels onto the grid: the first at u0 (0..1 across the whole picture), then du apart.</summary>
        public void Columns(float u0, float du, int count)
        {
            var w = Tone._w;
            if (X0.Length < count) { X0 = new int[count]; X1 = new int[count]; Fx = new float[count]; }
            for (var i = 0; i < count; i++)
            {
                var gx = Math.Clamp((u0 + i * du) * w - 0.5f, 0, w - 1);
                X0[i] = (int)gx;
                X1[i] = Math.Min(X0[i] + 1, w - 1);
                Fx[i] = gx - X0[i];
            }
            if (ARow.Length < w) { ARow = new float[w]; BRow = new float[w]; }
        }

        public void Row(ReadOnlySpan<float> log, float v, Span<float> gain) => Tone.Row(this, log, v, gain);
    }

    /// <summary>
    /// Brightness factors for one row of pixels (columns set by <see cref="Sampler.Columns"/>). <paramref name="log"/> holds
    /// their log brightness, and <paramref name="v"/> is the row's position down the whole picture (0..1). The base for each
    /// pixel is a·log + b, with (a, b) blended from the grid. Shadows lift the dark side by up to 2 stops, highlights move
    /// the bright side by up to 1.5 stops, and fine detail (log minus base) is kept.
    /// </summary>
    private void Row(Sampler s, ReadOnlySpan<float> log, float v, Span<float> gain)
    {
        var table = _gain;
        int[] cx0 = s.X0, cx1 = s.X1;
        float[] cfx = s.Fx, aRow = s.ARow, bRow = s.BRow;
        var gy = Math.Clamp(v * _h - 0.5f, 0, _h - 1);
        int y0 = (int)gy * _w, y1 = Math.Min((int)gy + 1, _h - 1) * _w;
        var fy = gy - (int)gy;
        for (var x = 0; x < _w; x++)
        {
            aRow[x] = _a[y0 + x] + (_a[y1 + x] - _a[y0 + x]) * fy;
            bRow[x] = _b[y0 + x] + (_b[y1 + x] - _b[y0 + x]) * fy;
        }
        const float scale = (TableSize - 1) / (TableHigh - TableLow);
        for (var i = 0; i < log.Length; i++)
        {
            int x0 = cx0[i], x1 = cx1[i];
            var fx = cfx[i];
            var a = aRow[x0] + (aRow[x1] - aRow[x0]) * fx;
            var b = bRow[x0] + (bRow[x1] - bRow[x0]) * fx;
            var f = Math.Clamp((a * log[i] + b - TableLow) * scale, 0, TableSize - 1.001f);
            var k = (int)f;
            gain[i] = table[k] + (table[k + 1] - table[k]) * (f - k);
        }
    }
}
