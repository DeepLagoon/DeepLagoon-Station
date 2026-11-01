using System.Linq;
using System.Numerics;
using System.Text;
using Content.Client.Inventory;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

public sealed partial class ClothingBodyFitSystem
{
    private const string HairSlot = "dl-hair-occlusion";

    public void RefreshHair(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        ClearSlot(uid, HairSlot);
        if (!TryComp<InventoryComponent>(uid, out var inventory)) return;
        if (_inventory.TryGetSlotEntity(uid, "head", out var item, inventory) &&
            TryComp<ClothingComponent>(item, out var clothing) &&
            TryComp<InventorySlotsComponent>(uid, out var slots) &&
            slots.VisualLayerKeys.TryGetValue("head", out var keys))
            ApplyHair(uid, sprite, clothing, keys, humanoid);
    }

    private void ApplyHair(EntityUid uid, SpriteComponent sprite, ClothingComponent clothing,
        HashSet<string> keys, HumanoidAppearanceComponent humanoid)
    {
        if (!clothing.HairOcclusion || humanoid.HiddenLayers.ContainsKey(HumanoidVisualLayers.Hair) ||
            humanoid.PermanentlyHidden.Contains(HumanoidVisualLayers.Hair)) return;

        var hats = new List<SpriteComponent.Layer>();
        foreach (var key in keys)
        {
            if (!sprite.LayerMapTryGet(key, out var index) ||
                sprite[index] is not SpriteComponent.Layer { Visible: true, ActualState: not null } layer ||
                layer.CopyToShaderParameters != null || key.EndsWith("-displacement")) continue;
            var source = new SpriteComponent.Layer(layer, sprite);
            // Face fitting pads the draw quad, not the source RSI. Use its original transform.
            if (_fits.TryGetValue(uid, out var fits))
                foreach (var fit in fits)
                    if (ReferenceEquals(fit.Layer, layer)) source.Scale = fit.Scale;
            hats.Add(source);
        }
        if (hats.Count == 0) return;

        var hairKeys = new HashSet<object> { HumanoidVisualLayers.Hair };
        foreach (var markings in humanoid.MarkingSet.Markings.Values)
        foreach (var marking in markings)
        {
            if (!marking.Visible || !_prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype) ||
                prototype.BodyPart != HumanoidVisualLayers.Hair) continue;
            foreach (var spec in prototype.Sprites)
                if (spec is SpriteSpecifier.Rsi rsi) hairKeys.Add($"{prototype.ID}-{rsi.RsiState}");
        }
        foreach (var key in hairKeys)
        {
            if (!sprite.LayerMapTryGet(key, out var index) ||
                sprite[index] is not SpriteComponent.Layer { Visible: true, ActualState: { } state } layer ||
                state.RsiDirections is not (RsiDirectionType.Dir4 or RsiDirectionType.Dir8) ||
                state.Size.X > 128 || state.Size.Y > 128 || state.DelayCount * state.Icons.Length > 64 ||
                layer.ShaderPrototype?.Id is not (null or "unshaded")) continue;
            var signature = new StringBuilder("hair|");
            AppendKey(signature, layer);
            foreach (var hat in hats) AppendKey(signature, hat);
            var mapKey = signature.ToString();
            if (!_maps.TryGetValue(mapKey, out var map))
            {
                map = BuildHairMap(layer, hats);
                _maps.Add(mapKey, map);
            }
            map.LastUse = ++_lastUse;
            if (map.Texture == null) continue;
            ApplyMap(uid, HairSlot, key, sprite, layer, null, new HashSet<string>(), map,
                layer.ShaderPrototype?.Id == "unshaded" ? "DeepLagoonHairOcclusionUnshaded" : ShaderId);
        }
        TrimCache();
    }

    private static Map BuildHairMap(SpriteComponent.Layer hair, List<SpriteComponent.Layer> hats)
    {
        var state = hair.ActualState!;
        var width = state.Size.X;
        var height = state.Size.Y;
        const int padding = 32;
        var directions = state.Icons.Length;
        using var atlas = new Image<Rgba32>(width, height * directions);
        var frames = new Dictionary<(RSI.State, int, int), Image<Rgba32>>();
        var changed = false;
        try
        {
            for (var direction = 0; direction < directions; direction++)
            {
                var worldDirection = direction;
                for (var d = 0; d < directions; d++)
                    if ((int) ((RsiDirection) d).OffsetRsiDir(hair.DirOffset) == direction) worldDirection = d;
                hair.GetLayerDrawMatrix((RsiDirection) worldDirection, out var hairMatrix);
                using var coverage = new Image<Rgba32>(width + padding * 2, height + padding * 2);
                foreach (var hat in hats)
                {
                    var hatState = hat.ActualState!;
                    var hatDirection = DirectionFor(hat, hatState, worldDirection);
                    var matrixDirection = hatState.RsiDirections == RsiDirectionType.Dir1 ? RsiDirection.South :
                        hatState.RsiDirections == RsiDirectionType.Dir4 ? ((RsiDirection) worldDirection).RoundToCardinal() : (RsiDirection) worldDirection;
                    hat.GetLayerDrawMatrix(matrixDirection, out var hatMatrix);
                    if (!Matrix3x2.Invert(hatMatrix, out var inverse)) continue;
                    var relative = hairMatrix * inverse;
                    // Union animated headwear frames; do not upload textures on animation ticks.
                    for (var frame = 0; frame < hatState.DelayCount; frame++)
                    {
                        var frameKey = (hatState, hatDirection, frame);
                        if (!frames.TryGetValue(frameKey, out var image))
                        {
                            image = TattooSpriteFrames.Read(hatState, hatDirection, frame);
                            frames.Add(frameKey, image);
                        }
                        var pixels = image.GetPixelSpan();
                        var output = coverage.GetPixelSpan();
                        for (var y = 0; y < coverage.Height; y++)
                        for (var x = 0; x < coverage.Width; x++)
                        {
                            var p = Vector2.Transform(new Vector2((x - padding + .5f - width / 2f) / EyeManager.PixelsPerMeter,
                                (height / 2f - y + padding - .5f) / EyeManager.PixelsPerMeter), relative);
                            var sx = (int) MathF.Floor(p.X * EyeManager.PixelsPerMeter + image.Width / 2f);
                            var sy = (int) MathF.Floor(image.Height / 2f - p.Y * EyeManager.PixelsPerMeter);
                            if (sx >= 0 && sy >= 0 && sx < image.Width && sy < image.Height &&
                                pixels[sy * image.Width + sx].A >= 128)
                                output[y * coverage.Width + x] = new Rgba32(255, 255, 255, 255);
                        }
                    }
                }
                using var map = ClothingHairOcclusionMap.Build(coverage, width, height, padding);
                var mapPixels = map.GetPixelSpan();
                var atlasPixels = atlas.GetPixelSpan();
                for (var i = 0; i < mapPixels.Length; i++)
                {
                    atlasPixels[direction * width * height + i] = mapPixels[i];
                    changed |= mapPixels[i].A == 0;
                }
            }
            if (!changed) return new Map(null, Vector2.One);
            return new Map((OwnedTexture) Texture.LoadFromImage(atlas, "hair-occlusion", new TextureLoadParameters
            { Srgb = false, SampleParameters = new TextureSampleParameters { Filter = false } }), Vector2.One);
        }
        finally { foreach (var image in frames.Values) image.Dispose(); }
    }
}