using Robust.Client.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>Fits the lower face without turning hair, horns or ears into helmet volume.</summary>
public static class ClothingFaceFitMap
{
    public static Image<Rgba32>? Build(Image<Rgba32> reference, Image<Rgba32> face,
        Image<Rgba32> garment, Image<Rgba32>? displacement, int padding, bool coversMouth = false)
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
        var source = new Rgba32[reference.Width * reference.Height];
        var coverage = new byte[source.Length];
        for (var y = 0; y < reference.Height; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            var d = displacement == null ? new Rgba32(128, 128, 0, 255)
                : displacementPixels[y * displacement.Height / reference.Height * displacement.Width + x * displacement.Width / reference.Width];
            var sx = x + d.R - 128;
            var sy = y + d.G - 128;
            if (sx < 0 || sx >= garment.Width || sy < 0 || sy >= garment.Height) continue;
            var index = y * reference.Width + x;
            source[index] = new Rgba32((byte) sx, (byte) sy, 128, d.A);
            coverage[index] = (byte) (garmentPixels[sy * garment.Width + sx].A * d.A / 255);
        }
        var mouthRow = bottom - Math.Max(1, (bottom - top + 1) / 3) + 1;
        var total = 0;
        var covered = 0;
        for (var y = mouthRow; y <= bottom; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            if (referencePixels[y * reference.Width + x].A < 128) continue;
            total++;
            if (coverage[y * reference.Width + x] >= 128) covered++;
        }
        // Cheek guards and the back of an open helmet can cover the attachment edges while
        // leaving the mouth free. Check the mouth itself before the shared fabric edge test.
        if (total == 0 || !coversMouth && covered * 2 < total) return null;
        using var anchor = reference.Clone();
        using var shape = face.Clone();
        var anchorPixels = anchor.GetPixelSpan();
        var shapePixels = shape.GetPixelSpan();
        // A vulp's nose starts above the lower half of the human head. Keeping only that
        // half discarded the upper muzzle, even when the rest of the map was installed.
        var mouthTop = top + (bottom - top + 1) / 3;
        for (var y = 0; y < reference.Height; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            var index = y * reference.Width + x;
            if (y < mouthTop)
            {
                anchorPixels[index] = default;
                shapePixels[index] = default;
            }
            // Muzzles are narrow straps, not solid respirators. Their covered attachment
            // still supplies the material to stretch, even if most of the jaw is uncovered.
            else if (coversMouth && coverage[index] < 128) anchorPixels[index] = default;
        }
        // The shared builder checks coverage at the attachment. A cap, raised visor or small
        // mouth-held accessory cannot stretch merely because its slot is named head or mask.
        var result = ClothingBodyFitMap.Build(reference, [new(shape, anchor, StretchVertically: false)],
            garment, displacement, padding);
        if (result == null) return null;

        var resultPixels = result.GetPixelSpan();
        for (var y = mouthTop; y < reference.Height; y++)
        for (var x = 0; x < reference.Width; x++)
        {
            var index = y * reference.Width + x;
            var destination = (y + padding) * result.Width + x + padding;
            // The added upper muzzle must not drag an existing eye opening sideways.
            if (y < top + (bottom - top + 1) / 2 && referencePixels[index].A >= 128)
            {
                resultPixels[destination] = source[index];
                continue;
            }
            if (shapePixels[index].A < 128 || referencePixels[index].A >= 128) continue;
            var sample = resultPixels[destination];
            if (sample.A >= 128 && garmentPixels[sample.G * garment.Width + sample.R].A >= 128) continue;

            // A protruding nose may lie a row ABOVE a muzzle's original strap. A horizontal
            // torso warp cannot reach it. Extend the nearest face fabric into that new volume;
            // holes over the original head (eyes, mouth openings) retain the shared warp.
            var nearest = -1;
            var distance = int.MaxValue;
            for (var sy = mouthTop; sy <= bottom; sy++)
            for (var sx = 0; sx < reference.Width; sx++)
            {
                var candidate = sy * reference.Width + sx;
                if (coverage[candidate] < 128 || referencePixels[candidate].A < 128) continue;
                var delta = Math.Abs(sx - x) + 2 * Math.Abs(sy - y);
                if (delta >= distance) continue;
                distance = delta;
                nearest = candidate;
            }
            if (nearest >= 0) resultPixels[destination] = source[nearest];
        }
        return result;
    }
}
