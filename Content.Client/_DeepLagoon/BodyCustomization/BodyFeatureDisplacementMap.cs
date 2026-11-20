using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>Authored body-coordinate warp shared by bare anatomy and clothing-fit masks.</summary>
public static class BodyFeatureDisplacementMap
{
    public const string VisualKey = "bodyFeatures";

    public static Image<Rgba32> Coordinates(int width, int height, Image<Rgba32> displacement)
    {
        var result = new Image<Rgba32>(width, height);
        var output = result.GetPixelSpan();
        var map = displacement.GetPixelSpan();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var d = map[y * displacement.Height / height * displacement.Width + x * displacement.Width / width];
            var sx = x + d.R - 128;
            var sy = y + d.G - 128;
            // Never sample a neighbouring RSI state when a large frame crosses a map edge.
            if (sx >= 0 && sy >= 0 && sx < width && sy < height)
                output[y * width + x] = new Rgba32((byte) sx, (byte) sy, 128, d.A);
        }
        return result;
    }

    public static Image<Rgba32> Apply(Image<Rgba32> source, Image<Rgba32> displacement)
    {
        using var coordinates = Coordinates(source.Width, source.Height, displacement);
        var result = new Image<Rgba32>(source.Width, source.Height);
        var points = coordinates.GetPixelSpan();
        var pixels = source.GetPixelSpan();
        var output = result.GetPixelSpan();
        for (var i = 0; i < output.Length; i++)
        {
            var point = points[i];
            if (point.A == 0) continue;
            var pixel = pixels[point.G * source.Width + point.R];
            pixel.A = (byte) (pixel.A * point.A / 255);
            output[i] = pixel;
        }
        return result;
    }
}
