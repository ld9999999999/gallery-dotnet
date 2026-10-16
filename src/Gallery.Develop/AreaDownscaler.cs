namespace Gallery.Develop;

/// <summary>
/// Shrinks an interleaved RGB float image as its rows arrive, with an exact area (box) filter: each output pixel is
/// the average of the source area it covers. So a full-size decode never has to sit in memory.
/// Feed every source row once, top to bottom; finished output rows go to the callback in order.
/// </summary>
public sealed class AreaDownscaler
{
    private readonly int _sw, _sh, _dw, _dh;
    private readonly Action<int, ReadOnlySpan<float>> _emit;
    private readonly int[] _col; private readonly float[] _colWeight;   // per source column: first output column, share of the pixel going to it
    private readonly int[] _row; private readonly float[] _rowWeight;   // the same for source rows
    private readonly float[] _reduced;                                   // the current source row, shrunk horizontally
    private float[] _acc, _next;                                         // output rows being summed: current and the one below
    private readonly float _norm;
    private int _y;

    public AreaDownscaler(int sourceWidth, int sourceHeight, int width, int height, Action<int, ReadOnlySpan<float>> emit)
    {
        if (width > sourceWidth || height > sourceHeight || width < 1 || height < 1) throw new ArgumentException("the downscaler only reduces");
        (_sw, _sh, _dw, _dh, _emit) = (sourceWidth, sourceHeight, width, height, emit);
        (_col, _colWeight) = Shares(sourceWidth, width);
        (_row, _rowWeight) = Shares(sourceHeight, height);
        _reduced = new float[width * 3];
        _acc = new float[width * 3];
        _next = new float[width * 3];
        _norm = (float)((double)width / sourceWidth * height / sourceHeight);
    }

    public int Width => _dw;
    public int Height => _dh;

    /// <summary>For each source pixel along one axis: the output pixel it starts in and the share of it that falls there.</summary>
    private static (int[] Index, float[] Share) Shares(int source, int target)
    {
        var index = new int[source];
        var share = new float[source];
        var step = (double)target / source; // output pixels per source pixel (at most 1)
        for (var i = 0; i < source; i++)
        {
            var start = i * step;
            var end = (i + 1) * step;
            var o = Math.Min((int)Math.Floor(start), target - 1);
            index[i] = o;
            share[i] = end <= o + 1 || o + 1 >= target ? 1f : (float)((o + 1 - start) / step);
        }
        return (index, share);
    }

    public void AddRow(ReadOnlySpan<float> source)
    {
        if (_y >= _sh) throw new InvalidOperationException("more rows than the source has");
        Array.Clear(_reduced);
        for (var x = 0; x < _sw; x++)
        {
            var o = _col[x] * 3;
            var w = _colWeight[x];
            float r = source[x * 3], g = source[x * 3 + 1], b = source[x * 3 + 2];
            _reduced[o] += r * w; _reduced[o + 1] += g * w; _reduced[o + 2] += b * w;
            if (w < 1f)
            {
                var v = 1f - w;
                _reduced[o + 3] += r * v; _reduced[o + 4] += g * v; _reduced[o + 5] += b * v;
            }
        }
        var wy = _rowWeight[_y];
        for (var i = 0; i < _reduced.Length; i++) _acc[i] += _reduced[i] * wy;
        if (wy < 1f) { var v = 1f - wy; for (var i = 0; i < _reduced.Length; i++) _next[i] += _reduced[i] * v; }
        var outRow = _row[_y];
        _y++;
        if (_y == _sh || _row[_y] != outRow)
        {
            for (var i = 0; i < _acc.Length; i++) _acc[i] *= _norm;
            _emit(outRow, _acc);
            (_acc, _next) = (_next, _acc);
            Array.Clear(_next);
        }
    }
}
