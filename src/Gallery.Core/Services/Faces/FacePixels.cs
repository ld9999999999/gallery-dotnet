using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Core.Services.Faces;

/// <summary>An 8-bit RGB picture in one flat array (R, G, B per pixel, row after row), for the face models.</summary>
public sealed class FacePixels
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public FacePixels(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length != width * height * 3) throw new ArgumentException("wrong buffer size", nameof(data));
    }

    public static FacePixels From(Image<Rgb24> img)
    {
        var p = new FacePixels(img.Width, img.Height);
        img.CopyPixelDataTo(MemoryMarshal.Cast<byte, Rgb24>(p.Data.AsSpan()));
        return p;
    }

    public Image<Rgb24> ToImage() => Image.LoadPixelData<Rgb24>(Data, Width, Height);
}
