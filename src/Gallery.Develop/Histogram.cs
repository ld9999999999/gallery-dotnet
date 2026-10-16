namespace Gallery.Develop;

/// <summary>Counts of the rendered 8-bit values, per channel and for brightness, and how many pixels are clipped.</summary>
public sealed class Histogram
{
    public int[] Red { get; } = new int[256];
    public int[] Green { get; } = new int[256];
    public int[] Blue { get; } = new int[256];
    /// <summary>Brightness of each pixel (Rec.709 weights on the display values).</summary>
    public int[] Luma { get; } = new int[256];
    /// <summary>Pixels counted: all of a small picture, every 4th of a large one.</summary>
    public int Pixels { get; internal set; }
    /// <summary>Pixels with a channel at 255.</summary>
    public int ClippedHighlights { get; internal set; }
    /// <summary>Pixels with a channel at 0.</summary>
    public int ClippedShadows { get; internal set; }

    internal void Clear()
    {
        Array.Clear(Red); Array.Clear(Green); Array.Clear(Blue); Array.Clear(Luma);
        Pixels = ClippedHighlights = ClippedShadows = 0;
    }
}
