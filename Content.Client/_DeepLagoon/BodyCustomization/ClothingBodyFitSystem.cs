using System.Linq;
using System.Numerics;
using System.Text;
using Content.Client._DeepLagoon.Tattoos;
using Content.Client.Inventory;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Armor;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Graphics;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>
/// Fits existing equipment layers to under-clothing anatomy. Geometry is cached and uploaded only on
/// appearance/equipment changes; the original RSI still supplies animation, direction, lighting and tint.
/// No RSI or texture shared with another character is modified.
/// </summary>
public sealed partial class ClothingBodyFitSystem : EntitySystem
{
    private const string ShaderId = "DeepLagoonClothingBodyFit";
    private const int IdleCacheLimit = 128;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Dictionary<string, Map> _maps = new();
    private readonly Dictionary<EntityUid, List<Fit>> _fits = new();
    private long _lastUse;

    private sealed class Map(OwnedTexture? texture, Vector2 scale)
    {
        public readonly OwnedTexture? Texture = texture;
        public readonly Vector2 Scale = scale;
        public int Users;
        public long LastUse;
    }

    private sealed record Fit(string Slot, string Key, SpriteComponent.Layer Layer, Vector2 Scale,
        ShaderInstance? OriginalShader, ProtoId<ShaderPrototype>? OriginalShaderId,
        SpriteComponent.Layer? Displacement, bool DisplacementVisible, string Helper,
        HashSet<string> Revealed, ShaderInstance Shader, Map Map);

    private sealed record Part(string Kind, List<SpriteComponent.Layer> Layers, List<RSI.State> Shapes, List<RSI.State> Anchors);

    public override void Initialize()
    {
        // Component lifecycle subscriptions belong to their owning systems. Observe removal without
        // competing with SpriteSystem or BodyCustomizationVisualizerSystem for an exclusive event.
        EntityManager.ComponentRemoved += OnComponentRemoved;
    }

    private void OnComponentRemoved(RemovedComponentEventArgs args)
    {
        if (args.BaseArgs.Component is SpriteComponent or HumanoidAppearanceComponent)
            Clear(args.BaseArgs.Owner);
    }

    public void Refresh(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        if (!TryComp<InventoryComponent>(uid, out var inventory) ||
            !TryComp<InventorySlotsComponent>(uid, out var slots)) return;
        var items = _inventory.GetSlotEnumerator((uid, inventory));
        while (items.NextItem(out var item, out var slot))
            if (TryComp<ClothingComponent>(item, out var clothing) &&
                slots.VisualLayerKeys.TryGetValue(slot.Name, out var keys))
                Apply(uid, item, slot.Name, sprite, clothing, keys, humanoid);
    }

    public void Apply(EntityUid uid, EntityUid item, string slot, SpriteComponent sprite,
        ClothingComponent clothing, HashSet<string> keys, HumanoidAppearanceComponent? humanoid = null)
    {
        ClearSlot(uid, slot);
        if (!Resolve(uid, ref humanoid, false)) return;
        if (slot is "head" or "mask" or "neck" or "neck2" or "neck3")
            ApplyFace(uid, item, slot, sprite, clothing, keys, humanoid);
        if (humanoid.PermanentlyHidden.Contains(HumanoidVisualLayers.Chest) ||
            !IsSoft(item, slot, clothing)) return;

        var parts = new List<Part>();
        foreach (var original in BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures))
        {
            var feature = BodyCustomizationCatalog.ForClothing(original);
            if (feature == null) continue;
            var sizes = BodyCustomizationCatalog.Parts[feature.Part][feature.Shape];
            var layers = new List<SpriteComponent.Layer>();
            var shapes = new List<RSI.State>();
            var anchors = new List<RSI.State>();
            var fallback = sizes[original.Size]
                .Select(side => sprite.LayerMapTryGet($"dl-body/{feature.Part}/{side}", out var index)
                    ? sprite[index] as SpriteComponent.Layer : null)
                .FirstOrDefault(layer => layer?.ActualState != null);
            if (fallback == null) continue;
            foreach (var side in sizes[feature.Size])
            {
                // A smaller sprite can have a rear layer even when the full-size sprite has none.
                var layer = sprite.LayerMapTryGet($"dl-body/{feature.Part}/{side}", out var index)
                    && sprite[index] is SpriteComponent.Layer { ActualState: not null } actual ? actual : fallback;
                var state = layer.ActualState!;
                // The smallest supported shape describes where the part attaches to the original body.
                var smallest = sizes.First(s => s.Key != "0" && s.Value.Contains(side)).Key;
                var attachment = feature.Clone();
                attachment.Size = smallest;
                if (!state.RSI.TryGetState(BodyCustomizationCatalog.SpriteState(feature, side, humanoid.GenitalAroused), out var shape) ||
                    !state.RSI.TryGetState(BodyCustomizationCatalog.SpriteState(attachment, side, humanoid.GenitalAroused), out var anchor))
                    continue;
                layers.Add(layer);
                shapes.Add(shape);
                anchors.Add(anchor);
            }
            if (layers.Count > 0) parts.Add(new Part(feature.Part, layers, shapes, anchors));
        }
        if (parts.Count == 0) return;
        var body = BodyLayers(humanoid, sprite, HumanoidVisualLayers.Chest, HumanoidVisualLayers.LLeg, HumanoidVisualLayers.RLeg);
        if (body.Count == 0) return;
        var hands = BodyLayers(humanoid, sprite, HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand);

        ApplyParts(uid, slot, sprite, keys, body, parts, hands);
    }

    private void ApplyParts(EntityUid uid, string slot, SpriteComponent sprite, HashSet<string> keys,
        List<SpriteComponent.Layer> body, List<Part> parts, List<SpriteComponent.Layer> hands)
    {
        var equipment = new List<(string Key, SpriteComponent.Layer Layer, SpriteComponent.Layer? Displacement)>();
        foreach (var key in keys.ToArray())
        {
            if (!sprite.LayerMapTryGet(key, out var index) ||
                sprite[index] is not SpriteComponent.Layer { ActualState: { } state } layer ||
                layer.CopyToShaderParameters != null ||
                state.RsiDirections is not (RsiDirectionType.Dir4 or RsiDirectionType.Dir8) ||
                state.Size.X > 64 || state.Size.Y > 64 || state.DelayCount * state.Icons.Length > 64)
                continue;
            var shaderName = layer.ShaderPrototype?.Id;
            if (shaderName is not (null or "StencilDraw" or "DisplacedStencilDraw")) continue;
            SpriteComponent.Layer? displacement = null;
            if (sprite.LayerMapTryGet(key + "-displacement", out var displacementIndex))
                displacement = sprite[displacementIndex] as SpriteComponent.Layer;
            if (displacement != null && (displacement.ActualState == null || displacement.ActualState.DelayCount != 1))
                continue;
            equipment.Add((key, layer, displacement));
        }
        // One garment can contain separate shirt, trousers, belt and insignia layers. They must use
        // the same fabric silhouette, otherwise each computes a different warp and opens seams.
        // Snapshot transforms before FitLayer scales any of the live layers for the padded quad.
        var fabric = equipment.Select(e => new SpriteComponent.Layer(e.Layer, sprite)).ToList();
        foreach (var (key, layer, displacement) in equipment)
        {
            try
            {
                FitLayer(uid, slot, key, sprite, layer, displacement, body, parts, fabric, hands, keys);
            }
            catch (Exception exception)
            {
                Log.Warning($"Could not fit clothing {key} on {ToPrettyString(uid)}: {exception.Message}");
                ClearSlot(uid, slot);
                break;
            }
        }
        TrimCache();
    }

    private List<SpriteComponent.Layer> BodyLayers(HumanoidAppearanceComponent humanoid, SpriteComponent sprite,
        params HumanoidVisualLayers[] parts)
    {
        var result = new List<SpriteComponent.Layer>();
        foreach (var part in parts)
        {
            if (humanoid.PermanentlyHidden.Contains(part)) continue;
            if (sprite.LayerMapTryGet(part, out var index) &&
                sprite[index] is SpriteComponent.Layer { ActualState: not null } native)
            { result.Add(native); continue; }
            // Species such as Synth build their body from markings. Their selected shells still
            // define the fit mask when the corresponding sprite layers are hidden by clothing.
            foreach (var marking in TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypes, part, visibleOnly: true))
                if (sprite[marking] is SpriteComponent.Layer { ActualState: not null } layer) result.Add(layer);
        }
        return result.Distinct().ToList();
    }

    private bool IsSoft(EntityUid item, string slot, ClothingComponent clothing)
    {
        if (slot is not ("jumpsuit" or "outerClothing" or "neck" or "neck2" or "neck3")) return false;
        return clothing.BodyFit switch
        {
            ClothingBodyFit.Soft => true,
            ClothingBodyFit.Rigid => false,
            // Lab coats, winter coats and ordinary thick fabric also use Armor for minor protection.
            // Substantial physical protection is treated as rigid unless the prototype explicitly opts in.
            _ => !TryComp<ArmorComponent>(item, out var armor) ||
                 !armor.Modifiers.Coefficients.Any(p => p.Key.Id is "Blunt" or "Slash" or "Piercing" && p.Value < .8f) &&
                 !armor.Modifiers.FlatReduction.Any(p => p.Key.Id is "Blunt" or "Slash" or "Piercing" && p.Value > 0),
        };
    }

    private void FitLayer(EntityUid uid, string slot, string key, SpriteComponent sprite,
        SpriteComponent.Layer layer, SpriteComponent.Layer? displacement, List<SpriteComponent.Layer> body,
        List<Part> parts, List<SpriteComponent.Layer> fabric, List<SpriteComponent.Layer> hands, HashSet<string> keys)
    {
        var signature = new StringBuilder().Append(sprite.NoRotation);
        AppendKey(signature, layer);
        if (displacement != null) AppendKey(signature, displacement);
        signature.Append("|fabric|");
        foreach (var f in fabric) AppendKey(signature, f);
        signature.Append("|body|");
        foreach (var b in body) AppendKey(signature, b);
        signature.Append("|hands|");
        foreach (var h in hands) AppendKey(signature, h);
        foreach (var part in parts)
        {
            signature.Append("|part|").Append(part.Kind);
            for (var i = 0; i < part.Layers.Count; i++)
            {
                AppendKey(signature, part.Layers[i], part.Shapes[i]);
                AppendKey(signature, part.Layers[i], part.Anchors[i]);
            }
        }
        var mapKey = signature.ToString();
        if (!_maps.TryGetValue(mapKey, out var map))
        {
            map = BuildMap(layer, displacement, body, parts, fabric, hands);
            _maps.Add(mapKey, map);
        }
        map.LastUse = ++_lastUse;
        if (map.Texture == null) return;
        var shader = _prototypes.Index<ShaderPrototype>(ShaderId).InstanceUnique();
        var helperKey = key + "-body-fit-source";
        var fit = new Fit(slot, key, layer, layer.Scale, layer.Shader, layer.ShaderPrototype,
            displacement, displacement?.Visible ?? false, helperKey, keys, shader, map);
        if (!_fits.TryGetValue(uid, out var fits)) _fits[uid] = fits = new List<Fit>();
        fits.Add(fit);
        map.Users++;
        if (layer.Shader != null) shader.Stencil = layer.Shader.Stencil;
        shader.SetParameter("fitMap", map.Texture);
        var state = layer.ActualState!;
        shader.SetParameter("frameSize", (Vector2) state.Size);
        shader.SetParameter("frameCount", state.DelayCount);
        shader.SetParameter("directionCount", state.Icons.Length);
        // Pack each rectangle into one uniform location to stay below the stock
        // client's signed-byte uniform cache limit, without reducing frame capacity.
        // Color arrays are converted to linear on upload; pre-encode numeric bounds.
        var bounds = new Color[64];
        for (var direction = 0; direction < state.Icons.Length; direction++)
        for (var frame = 0; frame < state.DelayCount; frame++)
        {
            var texture = state.GetFrame((RsiDirection) direction, frame);
            var region = texture is AtlasTexture atlas ? atlas.SubRegion : new UIBox2(0, 0, texture.Width, texture.Height);
            var backing = texture is AtlasTexture a ? a.SourceTexture : texture;
            var i = direction * state.DelayCount + frame;
            bounds[i] = Color.ToSrgb(new Color(
                region.Left / backing.Width, 1 - region.Bottom / backing.Height,
                region.Right / backing.Width, 1 - region.Top / backing.Height));
        }
        shader.SetParameter("directionBounds", bounds);
        // A non-drawing copy supplies the current frame's exact atlas bounds and dirties uniforms before
        // EVERY draw (also in multiple preview viewports). This avoids stale sampler bindings in Clyde.
        var helper = new SpriteComponent.Layer(layer, sprite)
        {
            CopyToShaderParameters = new SpriteComponent.CopyToShaderParameters(key) { ParameterUV = "sourceUV" },
        };
        sprite.LayerMapTryGet(key, out var index);
        var helperIndex = _sprites.AddLayer((uid, sprite), helper, index);
        sprite.LayerMapSet(helperKey, helperIndex);
        keys.Add(helperKey);
        if (displacement != null) displacement.Visible = false;
        sprite.LayerMapTryGet(key, out index);
        sprite.LayerSetShader(index, shader, ShaderId);
        sprite.LayerSetScale(index, fit.Scale * map.Scale);
    }

    private static void AppendKey(StringBuilder key, SpriteComponent.Layer layer, RSI.State? shape = null)
    {
        var state = shape ?? layer.ActualState;
        key.Append('|').Append(state?.RSI.Path).Append(':').Append(state?.StateId)
            .Append(':').Append(layer.Scale).Append(':').Append(layer.Offset).Append(':').Append(layer.Rotation)
            .Append(':').Append(layer.DirOffset);
    }

    private static Map BuildMap(SpriteComponent.Layer garment, SpriteComponent.Layer? displacement,
        List<SpriteComponent.Layer> body, List<Part> parts, List<SpriteComponent.Layer> fabric,
        List<SpriteComponent.Layer> hands)
    {
        var state = garment.ActualState!;
        var width = state.Size.X;
        var height = state.Size.Y;
        var padding = Math.Max(width, height) / 2;
        var directions = state.Icons.Length;
        using var atlas = new Image<Rgba32>(width + padding * 2, (height + padding * 2) * directions);
        var frames = new Dictionary<(RSI.State, int, int), Image<Rgba32>>();
        var changed = false;
        try
        {
            for (var direction = 0; direction < directions; direction++)
            {
                var worldDirection = direction;
                for (var d = 0; d < directions; d++)
                    if ((int) ((RsiDirection) d).OffsetRsiDir(garment.DirOffset) == direction) worldDirection = d;
                garment.GetLayerDrawMatrix((RsiDirection) worldDirection, out var garmentMatrix);
                using var skin = new Image<Rgba32>(width, height);
                var face = parts.FirstOrDefault(p => p.Kind == "face");
                foreach (var layer in body)
                    Composite(skin, layer, face?.Anchors[0] ?? layer.ActualState!, worldDirection, garmentMatrix);
                using var handMask = new Image<Rgba32>(width, height);
                foreach (var layer in hands) Composite(handMask, layer, layer.ActualState!, worldDirection, garmentMatrix);
                using var coverage = new Image<Rgba32>(width, height);
                foreach (var layer in fabric)
                {
                    var rsi = layer.ActualState!;
                    for (var frame = 0; frame < rsi.DelayCount; frame++)
                        Composite(coverage, layer, rsi, worldDirection, garmentMatrix, frame);
                }
                var shapes = new List<ClothingBodyFitMap.Anatomy>();
                var contours = new List<ClothingBodyFitMap.Contour>();
                try
                {
                    foreach (var part in parts)
                    {
                        var shape = new Image<Rgba32>(width, height);
                        var anchor = new Image<Rgba32>(width, height);
                        // Rear volume widens the existing trousers. Its small side attachment can
                        // lie under a long shirt, which must not be pulled down over the trouser legs.
                        shapes.Add(new(shape, anchor, StretchVertically: part.Kind != "butt"));
                        if (part.Kind == "breasts" && worldDirection == (int) RsiDirection.South ||
                            part.Kind == "butt" && worldDirection == (int) RsiDirection.North)
                            contours.Add(new ClothingBodyFitMap.Contour(shape, BelowWaist: part.Kind == "butt"));
                        for (var i = 0; i < part.Layers.Count; i++)
                        {
                            Composite(shape, part.Layers[i], part.Shapes[i], worldDirection, garmentMatrix);
                            Composite(anchor, part.Layers[i], part.Anchors[i], worldDirection, garmentMatrix);
                        }
                    }
                    var displaced = displacement?.ActualState is { } displacementState
                        ? Read(displacementState, DirectionFor(displacement!, displacementState, worldDirection), 0) : null;
                    using var fitted = face != null
                        ? ClothingFaceFitMap.Build(skin, shapes[0].Shape, coverage, displaced, padding)
                        : ClothingBodyFitMap.Build(skin, shapes, coverage, displaced, padding, hands: handMask, contours: contours);
                    changed |= fitted != null;
                    using var identity = fitted == null ? ClothingBodyFitMap.Build(skin, [], coverage, displaced, padding, true) : null;
                    var map = fitted ?? identity;
                    if (map == null) continue;
                    var pixels = map.GetPixelSpan();
                    var atlasPixels = atlas.GetPixelSpan();
                    for (var y = 0; y < map.Height; y++)
                    for (var x = 0; x < map.Width; x++)
                        atlasPixels[(direction * map.Height + y) * atlas.Width + x] = pixels[y * map.Width + x];
                }
                finally
                {
                    foreach (var anatomy in shapes) { anatomy.Shape.Dispose(); anatomy.Anchor.Dispose(); }
                }
            }
            if (!changed) return new Map(null, Vector2.One);
            var texture = (OwnedTexture) Texture.LoadFromImage(atlas, "clothing-body-fit", new TextureLoadParameters
            { Srgb = false, SampleParameters = new TextureSampleParameters { Filter = false } });
            return new Map(texture, new Vector2((width + padding * 2f) / width, (height + padding * 2f) / height));
        }
        finally
        {
            foreach (var image in frames.Values) image.Dispose();
        }

        Image<Rgba32> Read(RSI.State rsi, int direction, int frame)
        {
            if (frames.TryGetValue((rsi, direction, frame), out var image)) return image;
            image = TattooSpriteFrames.Read(rsi, direction, frame);
            frames.Add((rsi, direction, frame), image);
            return image;
        }

        void Composite(Image<Rgba32> output, SpriteComponent.Layer layer, RSI.State rsi, int worldDirection, Matrix3x2 targetMatrix, int frame = 0)
        {
            var direction = DirectionFor(layer, rsi, worldDirection);
            var image = Read(rsi, direction, frame);
            var matrixDirection = rsi.RsiDirections == RsiDirectionType.Dir1 ? RsiDirection.South :
                rsi.RsiDirections == RsiDirectionType.Dir4 ? ((RsiDirection) worldDirection).RoundToCardinal() : (RsiDirection) worldDirection;
            layer.GetLayerDrawMatrix(matrixDirection, out var matrix);
            if (!Matrix3x2.Invert(matrix, out var inverse)) return;
            var relative = targetMatrix * inverse;
            var pixels = image.GetPixelSpan();
            var outputPixels = output.GetPixelSpan();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = Vector2.Transform(new Vector2((x + .5f - width / 2f) / EyeManager.PixelsPerMeter,
                    (height / 2f - y - .5f) / EyeManager.PixelsPerMeter), relative);
                var sx = (int) MathF.Floor(p.X * EyeManager.PixelsPerMeter + image.Width / 2f);
                var sy = (int) MathF.Floor(image.Height / 2f - p.Y * EyeManager.PixelsPerMeter);
                if (sx >= 0 && sy >= 0 && sx < image.Width && sy < image.Height && pixels[sy * image.Width + sx].A >= 128)
                    outputPixels[y * output.Width + x] = new Rgba32(255, 255, 255, 255);
            }
        }
    }

    private static int DirectionFor(SpriteComponent.Layer layer, RSI.State state, int worldDirection)
    {
        if (state.RsiDirections == RsiDirectionType.Dir1) return 0;
        var direction = (RsiDirection) worldDirection;
        if (state.RsiDirections == RsiDirectionType.Dir4) direction = DirExt.RoundToCardinal(direction);
        return (int) direction.OffsetRsiDir(layer.DirOffset);
    }

    public void ClearSlot(EntityUid uid, string slot)
    {
        if (!_fits.TryGetValue(uid, out var fits)) return;
        TryComp<SpriteComponent>(uid, out var sprite);
        for (var i = fits.Count - 1; i >= 0; i--)
        {
            var fit = fits[i];
            if (fit.Slot != slot) continue;
            if (sprite != null)
            {
                if (sprite.LayerMapTryGet(fit.Helper, out var helper)) _sprites.RemoveLayer((uid, sprite), helper);
                if (sprite.LayerMapTryGet(fit.Key, out var index) && ReferenceEquals(sprite[index], fit.Layer))
                {
                    sprite.LayerSetScale(index, fit.Scale);
                    fit.Layer.Shader = fit.OriginalShader;
                    fit.Layer.ShaderPrototype = fit.OriginalShaderId;
                    if (fit.Displacement != null) fit.Displacement.Visible = fit.DisplacementVisible;
                }
            }
            fit.Revealed.Remove(fit.Helper);
            fit.Shader.Dispose();
            fit.Map.Users--;
            fit.Map.LastUse = ++_lastUse;
            fits.RemoveAt(i);
        }
        if (fits.Count == 0) _fits.Remove(uid);
        TrimCache();
    }

    private void Clear(EntityUid uid)
    {
        if (!_fits.TryGetValue(uid, out var fits)) return;
        foreach (var slot in fits.Select(f => f.Slot).Distinct().ToArray()) ClearSlot(uid, slot);
        TrimCache();
    }

    private void TrimCache()
    {
        if (_maps.Count <= IdleCacheLimit) return;
        foreach (var pair in _maps.Where(p => p.Value.Users == 0).OrderBy(p => p.Value.LastUse)
                     .Take(_maps.Count - IdleCacheLimit).ToArray())
        {
            pair.Value.Texture?.Dispose();
            _maps.Remove(pair.Key);
        }
    }

    public override void Shutdown()
    {
        EntityManager.ComponentRemoved -= OnComponentRemoved;
        foreach (var uid in _fits.Keys.ToArray()) Clear(uid);
        foreach (var map in _maps.Values) map.Texture?.Dispose();
        _maps.Clear();
        base.Shutdown();
    }
}
