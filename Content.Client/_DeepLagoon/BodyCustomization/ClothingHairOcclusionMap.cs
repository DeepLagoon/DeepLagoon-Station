using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>Identity sampling map that leaves hair visible only below the headwear's lower contour.</summary>
public static class ClothingHairOcclusionMap
{
    public static Image<Rgba32> Build(Image<Rgba32> coverage, int width, int height, int padding = 0)
    {
        var pixels = coverage.GetPixelSpan();
        var bottom = new int[coverage.Width];
        Array.Fill(bottom, -1);
        var lowest = -1;
        for (var y = 0; y < coverage.Height; y++)
        for (var x = 0; x < coverage.Width; x++)
        {
            if (pixels[y * coverage.Width + x].A < 128) continue;
            bottom[x] = y;
            lowest = y;
        }

        var map = new Image<Rgba32>(width, height);
        var output = map.GetPixelSpan();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Outside the hat's columns, only hair below the entire hat can remain visible.
            var edge = bottom[x + padding] >= 0 ? bottom[x + padding] : lowest;
            output[y * width + x] = new Rgba32((byte) x, (byte) y, 128,
                (byte) (y + padding > edge ? 255 : 0));
        }
        return map;
    }
}