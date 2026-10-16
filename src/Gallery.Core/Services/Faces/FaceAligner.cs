namespace Gallery.Core.Services.Faces;

/// <summary>
/// Lines up a face with ArcFace's 112 × 112 template: finds the rotation, scale and shift (no mirroring) that best map
/// the face's five landmarks onto the template points, then warps bilinearly (black outside the picture). Matches
/// insightface's face_align.norm_crop; for five points without mirroring, Umeyama's estimate gives the same least-squares fit.
/// </summary>
public static class FaceAligner
{
    public const int Size = 112;

    /// <summary>Template landmarks: eyes, nose tip, mouth corners (left and right as seen).</summary>
    public static readonly float[] Template =
    {
        38.2946f, 51.6963f,
        73.5318f, 51.5014f,
        56.0252f, 71.7366f,
        41.5493f, 92.3655f,
        70.7299f, 92.2041f,
    };

    /// <summary>Eye distance in the template. A face with fewer pixels between its eyes gets enlarged.</summary>
    public static readonly float TemplateEyeDistance = Template[2] - Template[0];

    /// <summary>The similarity transform dst = [a −b; b a]·src + (tx, ty) that best maps the landmarks onto the template.</summary>
    public static (double A, double B, double Tx, double Ty) Estimate(float[] landmarks)
    {
        double msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (var i = 0; i < 5; i++) { msx += landmarks[2 * i]; msy += landmarks[2 * i + 1]; mdx += Template[2 * i]; mdy += Template[2 * i + 1]; }
        msx /= 5; msy /= 5; mdx /= 5; mdy /= 5;
        double num1 = 0, num2 = 0, den = 0;
        for (var i = 0; i < 5; i++)
        {
            double sx = landmarks[2 * i] - msx, sy = landmarks[2 * i + 1] - msy;
            double dx = Template[2 * i] - mdx, dy = Template[2 * i + 1] - mdy;
            num1 += sx * dx + sy * dy;
            num2 += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }
        if (den <= 0) throw new ArgumentException("landmarks coincide", nameof(landmarks));
        var a = num1 / den;
        var b = num2 / den;
        return (a, b, mdx - (a * msx - b * msy), mdy - (b * msx + a * msy));
    }

    /// <summary>The 112 × 112 crop of the face whose landmarks are given in <paramref name="img"/>'s pixels.</summary>
    public static FacePixels Crop(FacePixels img, float[] landmarks)
    {
        var (a, b, tx, ty) = Estimate(landmarks);
        // Sampling maps each crop pixel back: src = R⁻¹ (dst − t), R⁻¹ = [a b; −b a] / (a² + b²)
        var k = 1 / (a * a + b * b);
        var crop = new FacePixels(Size, Size);
        var src = img.Data;
        int w = img.Width, h = img.Height;
        for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                double du = u - tx, dv = v - ty;
                var x = k * (a * du + b * dv);
                var y = k * (-b * du + a * dv);
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                float fx = (float)(x - x0), fy = (float)(y - y0);
                var o = (v * Size + u) * 3;
                for (var c = 0; c < 3; c++)
                {
                    float p00 = Px(src, w, h, x0, y0, c), p10 = Px(src, w, h, x0 + 1, y0, c);
                    float p01 = Px(src, w, h, x0, y0 + 1, c), p11 = Px(src, w, h, x0 + 1, y0 + 1, c);
                    var value = (p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy;
                    crop.Data[o + c] = (byte)Math.Clamp(value + 0.5f, 0, 255);
                }
            }
        return crop;
    }

    private static float Px(byte[] d, int w, int h, int x, int y, int c) =>
        (uint)x < (uint)w && (uint)y < (uint)h ? d[(y * w + x) * 3 + c] : 0f;
}
