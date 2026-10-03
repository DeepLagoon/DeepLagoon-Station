using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>One shared, nearest-sampled contact texture; floor clipping never requires a viewport mask.</summary>
internal static class AmbientOcclusionMobSpot
{
    private const int Size = AmbientOcclusionMask.Steps;

    public static Rgba32[] MakePixels()
    {
        var pixels = new Rgba32[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var delta = new Vector2((x + 0.5f) / Size * 2 - 1, (y + 0.5f) / Size * 2 - 1);
            var falloff = Math.Max(0, 1 - delta.LengthSquared());
            var alpha = MathF.Round(falloff * falloff * 8) / 8 * 0.22f;
            pixels[y * Size + x].A = (byte) MathF.Round(alpha * 255);
        }
        return pixels;
    }

    public static bool TryClip(Box2 footprint, Box2 floor, out Box2 quad, out UIBox2 source)
    {
        var left = Math.Max(footprint.Left, floor.Left);
        var right = Math.Min(footprint.Right, floor.Right);
        var bottom = Math.Max(footprint.Bottom, floor.Bottom);
        var top = Math.Min(footprint.Top, floor.Top);
        quad = new Box2(left, bottom, right, top);
        source = default;
        if (right <= left || top <= bottom || footprint.Width <= 0 || footprint.Height <= 0) return false;
        source = new UIBox2((left - footprint.Left) / footprint.Width * Size,
            (footprint.Top - top) / footprint.Height * Size,
            (right - footprint.Left) / footprint.Width * Size,
            (footprint.Top - bottom) / footprint.Height * Size);
        return true;
    }
}
