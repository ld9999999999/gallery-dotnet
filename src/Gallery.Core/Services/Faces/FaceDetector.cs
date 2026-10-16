using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Services.Faces;

/// <summary>A face found in a picture, in that picture's pixels: box, detector confidence and five landmarks (eyes, nose tip, mouth corners).</summary>
public sealed record DetectedFace(float X1, float Y1, float X2, float Y2, float Score, float[] Landmarks)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;

    /// <summary>Distance between the eyes, as a measure of how many pixels the face has.</summary>
    public float EyeDistance => MathF.Sqrt(MathF.Pow(Landmarks[2] - Landmarks[0], 2) + MathF.Pow(Landmarks[3] - Landmarks[1], 2));

    public DetectedFace Scaled(float s) => new(X1 * s, Y1 * s, X2 * s, Y2 * s, Score, Landmarks.Select(v => v * s).ToArray());
}

/// <summary>
/// InsightFace's SCRFD detector (det_10g.onnx, det_500m.onnx), decoded the same way as insightface's scrfd.py:
/// three strides (8, 16, 32) -> two anchors per cell -> box side distances and landmark offsets in stride units ->
/// score threshold -> non-maximum suppression.
/// The picture is scaled so its long edge is the detector size, then padded right and bottom with black to a multiple of
/// 32, or to a square for backends that need fixed shapes (CoreML).
/// </summary>
public static class FaceDetector
{
    private static readonly int[] Strides = { 8, 16, 32 };
    private const int AnchorsPerCell = 2;
    public const float ScoreThreshold = 0.5f;
    public const float NmsThreshold = 0.4f;

    /// <summary>Builds the detector's input: the picture scaled and padded, as (value − 127.5) / 128 in RGB planes.</summary>
    public sealed record Input(float[] Data, int Width, int Height, float Scale);

    public static Input Prepare(FacePixels img, int size, bool square)
    {
        var scale = (float)size / Math.Max(img.Width, img.Height);
        var w = Math.Max(1, (int)Math.Round(img.Width * scale));
        var h = Math.Max(1, (int)Math.Round(img.Height * scale));
        var resized = w == img.Width && h == img.Height ? img : Resize(img, w, h);
        int cw = square ? size : RoundUp32(w), ch = square ? size : RoundUp32(h);
        var data = new float[3 * ch * cw];
        Array.Fill(data, -127.5f / 128f); // black, because insightface pads before normalising
        var plane = ch * cw;
        var px = resized.Data;
        for (var y = 0; y < h; y++)
        {
            var row = y * cw;
            var src = y * w * 3;
            for (var x = 0; x < w; x++, src += 3)
            {
                data[row + x] = (px[src] - 127.5f) / 128f;
                data[plane + row + x] = (px[src + 1] - 127.5f) / 128f;
                data[2 * plane + row + x] = (px[src + 2] - 127.5f) / 128f;
            }
        }
        return new Input(data, cw, ch, scale);
    }

    /// <summary>
    /// Decodes faces from the model's nine outputs (scores, then box distances, then landmark offsets, for strides 8, 16
    /// and 32), in the original picture's pixel coordinates.
    /// </summary>
    public static List<DetectedFace> Decode(IReadOnlyList<float[]> outputs, int inputWidth, int inputHeight, float scale,
        float scoreThreshold = ScoreThreshold, float nmsThreshold = NmsThreshold)
    {
        if (outputs.Count != 9) throw new ArgumentException($"SCRFD with landmarks has 9 outputs, got {outputs.Count}");
        var found = new List<DetectedFace>();
        for (var si = 0; si < Strides.Length; si++)
        {
            var stride = Strides[si];
            var scores = outputs[si];
            var boxes = outputs[si + 3];
            var kps = outputs[si + 6];
            int gw = inputWidth / stride, gh = inputHeight / stride;
            var n = gw * gh * AnchorsPerCell;
            if (scores.Length != n || boxes.Length != n * 4 || kps.Length != n * 10)
                throw new InvalidDataException($"stride {stride}: outputs don't fit a {gw}×{gh} grid");
            for (var i = 0; i < n; i++)
            {
                var score = scores[i];
                if (score < scoreThreshold) continue;
                var cell = i / AnchorsPerCell;
                float cx = cell % gw * stride, cy = cell / gw * stride;
                var lm = new float[10];
                for (var j = 0; j < 5; j++)
                {
                    lm[2 * j] = (cx + kps[i * 10 + 2 * j] * stride) / scale;
                    lm[2 * j + 1] = (cy + kps[i * 10 + 2 * j + 1] * stride) / scale;
                }
                found.Add(new DetectedFace(
                    (cx - boxes[i * 4] * stride) / scale, (cy - boxes[i * 4 + 1] * stride) / scale,
                    (cx + boxes[i * 4 + 2] * stride) / scale, (cy + boxes[i * 4 + 3] * stride) / scale, score, lm));
            }
        }
        return Nms(found, nmsThreshold);
    }

    /// <summary>Non-maximum suppression: goes through faces by descending score and drops any that overlaps a kept face by more than <paramref name="threshold"/> (IoU).</summary>
    public static List<DetectedFace> Nms(List<DetectedFace> faces, float threshold)
    {
        var sorted = faces.OrderByDescending(f => f.Score).ToList();
        var keep = new List<DetectedFace>();
        foreach (var f in sorted)
        {
            var overlaps = false;
            foreach (var k in keep)
            {
                var iw = Math.Max(0, Math.Min(f.X2, k.X2) - Math.Max(f.X1, k.X1) + 1);
                var ih = Math.Max(0, Math.Min(f.Y2, k.Y2) - Math.Max(f.Y1, k.Y1) + 1);
                var inter = iw * ih;
                var union = (f.Width + 1) * (f.Height + 1) + (k.Width + 1) * (k.Height + 1) - inter;
                if (inter / union > threshold) { overlaps = true; break; }
            }
            if (!overlaps) keep.Add(f);
        }
        return keep;
    }

    /// <summary>Runs the model on a prepared input and decodes the faces.</summary>
    internal static List<DetectedFace> Run(InferenceSession session, Input input)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(input.Data, new long[] { 1, 3, input.Height, input.Width });
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, new[] { session.InputNames[0] }, new[] { value }, session.OutputNames);
        if (results.Count != 9) throw new InvalidDataException($"the detector has {results.Count} outputs; expected SCRFD with landmarks (9)");
        var outputs = results.Select(r => r.GetTensorDataAsSpan<float>().ToArray()).ToList();
        return Decode(outputs, input.Width, input.Height, input.Scale);
    }

    private static int RoundUp32(int v) => (v + 31) / 32 * 32;

    private static FacePixels Resize(FacePixels img, int w, int h)
    {
        using var i = img.ToImage();
        i.Mutate(x => x.Resize(w, h, KnownResamplers.Triangle));
        return FacePixels.From(i);
    }
}
