using System.Linq;
using System.Numerics;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.Tattoos;

/// <summary>Snapshots actual skin, markings and cosmetic anatomy, without clothing or ink.</summary>
public static class TattooBodyPreview
{
    public static bool TrySource(HumanoidAppearanceComponent humanoid, SpriteComponent sprite, IPrototypeManager prototypes,
        HumanoidVisualLayers part, out int index)
    {
        if (sprite.LayerMapTryGet(part, out index) && ((SpriteComponent.Layer)sprite[index]).ActualState != null) return true;
        foreach (var layer in MarkingLayers(humanoid, sprite, prototypes, part, visibleOnly: true))
            if (((SpriteComponent.Layer)sprite[layer]).ActualState != null) { index = layer; return true; }
        index = -1;
        return false;
    }

    public static int[] ContourLayers(HumanoidAppearanceComponent humanoid, SpriteComponent sprite, IPrototypeManager prototypes,
        HumanoidVisualLayers part, IEnumerable<int> anatomy)
        => anatomy.Concat(sprite.LayerMapTryGet(part, out var index) && ((SpriteComponent.Layer)sprite[index]).ActualState != null
            ? Array.Empty<int>() : MarkingLayers(humanoid, sprite, prototypes, part, visibleOnly: true)).Distinct().ToArray();

    public static int[] MarkingLayers(HumanoidAppearanceComponent humanoid, SpriteComponent sprite,
        IPrototypeManager prototypes, HumanoidVisualLayers? part = null, bool visibleOnly = false)
    {
        var layers = new HashSet<int>();
        foreach (var list in humanoid.MarkingSet.Markings.Values)
        foreach (var marking in list)
        {
            if (visibleOnly && !marking.Visible || !prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype)
                || part != null && prototype.BodyPart != part) continue;
            foreach (var spec in prototype.Sprites)
                if (spec is SpriteSpecifier.Rsi rsi && sprite.LayerMapTryGet($"{prototype.ID}-{rsi.RsiState}", out var index)) layers.Add(index);
        }
        return layers.Order().ToArray();
    }

    public static string Key(SpriteComponent sprite, IEnumerable<int> layers, int direction, bool animation = true) => direction + "/" + sprite.Color.ToHex() + "/" +
        string.Join(';', layers.Select(i => {
            var l = (SpriteComponent.Layer)sprite[i];
            return $"{l.ActualRsi?.Path}/{l.State}/{(animation ? l.AnimationFrame : 0)}/{l.Color.ToHex()}/{l.Scale}/{l.Offset}/{l.Rotation}/{l.DirOffset}";
        }));

    public static Image<Rgba32> Render(SpriteComponent sprite, int bodyIndex, IEnumerable<int> markingIndices, int direction,
        IEnumerable<int>? anatomyIndices = null)
    {
        var body = (SpriteComponent.Layer)sprite[bodyIndex];
        var state = body.ActualState ?? throw new InvalidOperationException("The body layer has no RSI state.");
        var result = new Image<Rgba32>(state.Size.X, state.Size.Y);
        var bodyDirection = DirectionFor(body, direction);
        body.GetLayerDrawMatrix(unchecked((RsiDirection)bodyDirection), out var bodyMatrix);
        var anatomy = anatomyIndices?.ToArray() ?? [];
        foreach (var index in new[] { bodyIndex }.Concat(markingIndices).Concat(anatomy).Distinct().Order())
        {
            var layer = (SpriteComponent.Layer)sprite[index];
            if (layer.ActualState == null) continue;
            Composite(result, layer, DirectionFor(layer, direction), bodyMatrix, sprite);
        }
        // The contour includes selected anatomy, while unrelated markings remain clipped.
        using var skin = ReadSkin(sprite, bodyIndex, direction, anatomy);
        var dst = result.GetPixelSpan(); var mask = skin.GetPixelSpan();
        for (var i = 0; i < dst.Length; i++) if (mask[i].A == 0) dst[i] = default;
        return result;
    }

    public static Image<Rgba32> ReadSkin(SpriteComponent sprite, int bodyIndex, int direction, IEnumerable<int>? anatomyIndices = null)
    {
        var layer = (SpriteComponent.Layer)sprite[bodyIndex]; var state = layer.ActualState!;
        var skin = TattooSpriteFrames.Read(state, DirectionFor(layer, direction), Math.Clamp(layer.AnimationFrame, 0, state.DelayCount - 1));
        if (anatomyIndices != null)
        {
            layer.GetLayerDrawMatrix(unchecked((RsiDirection)DirectionFor(layer, direction)), out var matrix);
            foreach (var index in anatomyIndices.Where(index => index != bodyIndex))
            {
                var anatomy = (SpriteComponent.Layer)sprite[index];
                if (anatomy.ActualState != null) Composite(skin, anatomy, DirectionFor(anatomy, direction), matrix, sprite);
            }
        }
        return skin;
    }

    public static Image<Rgba32> ReadAnatomy(SpriteComponent sprite, int bodyIndex, int direction, IEnumerable<int> anatomyIndices)
    {
        var body = (SpriteComponent.Layer)sprite[bodyIndex]; var state = body.ActualState!;
        var mask = new Image<Rgba32>(state.Size.X, state.Size.Y);
        body.GetLayerDrawMatrix(unchecked((RsiDirection)DirectionFor(body, direction)), out var matrix);
        foreach (var index in anatomyIndices)
        {
            var layer = (SpriteComponent.Layer)sprite[index];
            if (layer.ActualState != null) Composite(mask, layer, DirectionFor(layer, direction), matrix, sprite);
        }
        return mask;
    }

    private static int DirectionFor(SpriteComponent.Layer layer, int direction)
    {
        var state = layer.ActualState!;
        return (int)unchecked((RsiDirection)(state.RsiDirections == RsiDirectionType.Dir1 ? 0 : direction)).OffsetRsiDir(layer.DirOffset);
    }

    private static void Composite(Image<Rgba32> output, SpriteComponent.Layer layer, int direction, Matrix3x2 bodyMatrix, SpriteComponent sprite)
    {
        var state = layer.ActualState!;
        using var image = TattooSpriteFrames.Read(state, direction, Math.Clamp(layer.AnimationFrame, 0, state.DelayCount - 1));
        layer.GetLayerDrawMatrix(unchecked((RsiDirection)direction), out var matrix);
        if (!Matrix3x2.Invert(matrix, out var inverse)) return;
        var relative = bodyMatrix * inverse;
        var tint = layer.Color * sprite.Color;
        var source = image.GetPixelSpan(); var destination = output.GetPixelSpan();
        for (var y = 0; y < output.Height; y++) for (var x = 0; x < output.Width; x++)
        {
            var p = Vector2.Transform(new Vector2((x + .5f - output.Width / 2f) / EyeManager.PixelsPerMeter,
                (output.Height / 2f - y - .5f) / EyeManager.PixelsPerMeter), relative);
            var sx = unchecked((int)MathF.Floor(p.X * EyeManager.PixelsPerMeter + image.Width / 2f));
            var sy = unchecked((int)MathF.Floor(image.Height / 2f - p.Y * EyeManager.PixelsPerMeter));
            if (sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height) continue;
            var pixel = source[sy * image.Width + sx]; var old = destination[y * output.Width + x];
            var a = Math.Clamp(pixel.A / 255f * tint.A, 0, 1); var oldA = old.A / 255f;
            var alpha = a + oldA * (1 - a); if (alpha <= 0) continue;
            destination[y * output.Width + x] = new Rgba32(
                unchecked((byte)Math.Clamp((pixel.R * tint.R * a + old.R * oldA * (1 - a)) / alpha, 0, 255)),
                unchecked((byte)Math.Clamp((pixel.G * tint.G * a + old.G * oldA * (1 - a)) / alpha, 0, 255)),
                unchecked((byte)Math.Clamp((pixel.B * tint.B * a + old.B * oldA * (1 - a)) / alpha, 0, 255)),
                unchecked((byte)(alpha * 255)));
        }
    }
}
