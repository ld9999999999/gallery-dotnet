using Microsoft.ML.OnnxRuntime;

namespace Gallery.Core.Services.Faces;

/// <summary>
/// ArcFace fingerprints (w600k_r50.onnx, w600k_mbf.onnx): a 112 × 112 aligned face goes in, 512 numbers come out, scaled
/// to length 1 so the cosine similarity of two faces is their dot product. Input is (value − 127.5) / 127.5 in RGB
/// planes, done outside the model because these PyTorch exports don't scale it themselves. One face per call, since batches are slower on the
/// CPU (docs/faces.md).
/// </summary>
public static class FaceRecognizer
{
    public const int Dimensions = 512;
    private const int Plane = FaceAligner.Size * FaceAligner.Size;

    public static float[] Prepare(FacePixels crop)
    {
        if (crop.Width != FaceAligner.Size || crop.Height != FaceAligner.Size) throw new ArgumentException("not an aligned 112 × 112 face", nameof(crop));
        var data = new float[3 * Plane];
        var px = crop.Data;
        for (var i = 0; i < Plane; i++)
        {
            data[i] = (px[3 * i] - 127.5f) / 127.5f;
            data[Plane + i] = (px[3 * i + 1] - 127.5f) / 127.5f;
            data[2 * Plane + i] = (px[3 * i + 2] - 127.5f) / 127.5f;
        }
        return data;
    }

    internal static float[] Run(InferenceSession session, FacePixels crop)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(Prepare(crop), new long[] { 1, 3, FaceAligner.Size, FaceAligner.Size });
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, new[] { session.InputNames[0] }, new[] { value }, new[] { session.OutputNames[0] });
        var output = results[0].GetTensorDataAsSpan<float>();
        if (output.Length != Dimensions) throw new InvalidDataException($"the recogniser gave {output.Length} numbers; expected {Dimensions}");
        return Normalised(output.ToArray());
    }

    public static float[] Normalised(float[] v)
    {
        var len = MathF.Sqrt(v.Sum(x => x * x));
        if (len > 0) for (var i = 0; i < v.Length; i++) v[i] /= len;
        return v;
    }

    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => System.Numerics.Tensors.TensorPrimitives.Dot(a, b);
}
