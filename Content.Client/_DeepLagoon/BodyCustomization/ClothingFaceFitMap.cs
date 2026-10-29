using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>Fits the lower face without turning hair, horns or ears into helmet volume.</summary>
public static class ClothingFaceFitMap
{
    public static Image<Rgba32>? Build(Image<Rgba32> reference, Image<Rgba32> face,
        Image<Rgba32> garment, Image<Rgba32>? displacement, int padding)
    {
        var top = reference.Height;
        var bottom = -1;
        var referencePixels = reference.GetPixelSpan();
        for (var y = 0; y < reference.Height; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            if (referencePixels[y * reference.Width + x].A < 128) continue;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        if (bottom < top) return null;
        var garmentPixels = garment.GetPixelSpan();
        var displacementPixels = displacement != null ? displacement.GetPixelSpan() : default;
        var mouthRow = bottom - Math.Max(1, (bottom - top + 1) / 3) + 1;
        var total = 0;
        var covered = 0;
        for (var y = mouthRow; y <= bottom; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            if (referencePixels[y * reference.Width + x].A < 128) continue;
            total++;
            var d = displacement == null ? new Rgba32(128, 128, 0, 255)
                : displacementPixels[y * displacement.Height / reference.Height * displacement.Width + x * displacement.Width / reference.Width];
            var sx = x + d.R - 128;
            var sy = y + d.G - 128;
            if (sx >= 0 && sx < garment.Width && sy >= 0 && sy < garment.Height &&
                garmentPixels[sy * garment.Width + sx].A * d.A / 255 >= 128) covered++;
        }
        // Cheek guards and the back of an open helmet can cover the attachment edges while
        // leaving the mouth free. Check the mouth itself before the shared fabric edge test.
        if (total == 0 || covered * 2 < total) return null;
        using var anchor = reference.Clone();
        using var shape = face.Clone();
        var anchorPixels = anchor.GetPixelSpan();
        var shapePixels = shape.GetPixelSpan();
        var mouthTop = top + (bottom - top + 1) / 2;
        for (var y = 0; y < mouthTop; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            anchorPixels[y * reference.Width + x] = default;
            shapePixels[y * reference.Width + x] = default;
        }
        // The shared builder checks coverage at the attachment. A cap, raised visor or small
        // mouth-held accessory cannot stretch merely because its slot is named head or mask.
        return ClothingBodyFitMap.Build(reference, [new(shape, anchor, StretchVertically: false)],
            garment, displacement, padding);
    }
}
