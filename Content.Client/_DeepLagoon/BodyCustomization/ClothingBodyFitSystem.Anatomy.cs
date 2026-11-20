using System.Numerics;
using System.Text;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.Utility;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.BodyCustomization;

public sealed partial class ClothingBodyFitSystem
{
    private const string AnatomySlot = "dl-body-displacement";

    private RSI.State? AnatomyDisplacement(EntityUid uid, Sex sex, int frameWidth)
    {
        if (!TryComp<InventoryComponent>(uid, out var inventory)) return null;
        var data = inventory.Displacements.GetValueOrDefault(BodyFeatureDisplacementMap.VisualKey);
        var alternate = sex == Sex.Female ? inventory.FemaleDisplacements : inventory.MaleDisplacements;
        if (sex != Sex.Unsexed && alternate.TryGetValue(BodyFeatureDisplacementMap.VisualKey, out var selected)) data = selected;
        if (data == null || !data.SizeMaps.TryGetValue(frameWidth, out var layer) && !data.SizeMaps.TryGetValue(32, out layer) ||
            layer.RsiPath == null || layer.State == null) return null;
        var rsi = _faceResources.GetResource<RSIResource>(new ResPath("/Textures") / layer.RsiPath).RSI;
        return rsi.TryGetState(layer.State, out var state) ? state : null;
    }

    public void ClearAnatomyDisplacement(EntityUid uid) => ClearSlot(uid, AnatomySlot);

    public void ApplyAnatomyDisplacement(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite,
        IEnumerable<string> keys)
    {
        var helpers = new HashSet<string>();
        foreach (var key in keys)
        {
            if (!sprite.LayerMapTryGet(key, out var index) || sprite[index] is not SpriteComponent.Layer { ActualState: { } state } layer ||
                state.Size.X > 64 || state.Size.Y > 64 || state.DelayCount * state.Icons.Length > 64 ||
                AnatomyDisplacement(uid, humanoid.Sex, state.Size.X) is not { } displacement) continue;
            var signature = new StringBuilder("anatomy|");
            AppendKey(signature, layer);
            signature.Append('|').Append(displacement.RSI.Path).Append(':').Append(displacement.StateId);
            var cacheKey = signature.ToString();
            if (!_maps.TryGetValue(cacheKey, out var map))
            {
                using var atlas = new Image<Rgba32>(state.Size.X, state.Size.Y * state.Icons.Length);
                for (var direction = 0; direction < state.Icons.Length; direction++)
                {
                    var worldDirection = direction;
                    for (var d = 0; d < state.Icons.Length; d++)
                        if ((int) ((RsiDirection) d).OffsetRsiDir(layer.DirOffset) == direction) worldDirection = d;
                    using var sourceMap = TattooSpriteFrames.Read(displacement, DirectionFor(layer, displacement, worldDirection));
                    using var coordinates = BodyFeatureDisplacementMap.Coordinates(state.Size.X, state.Size.Y, sourceMap);
                    var pixels = coordinates.GetPixelSpan(); var output = atlas.GetPixelSpan();
                    for (var i = 0; i < pixels.Length; i++) output[direction * pixels.Length + i] = pixels[i];
                }
                var texture = (OwnedTexture) Texture.LoadFromImage(atlas, "body-feature-displacement", new TextureLoadParameters
                { Srgb = false, SampleParameters = new TextureSampleParameters { Filter = false } });
                // No padding and no layer transform changes: clothing Composite reads these
                // layers in their original local matrix, then applies the same authored map.
                _maps[cacheKey] = map = new Map(texture, Vector2.One);
            }
            map.LastUse = ++_lastUse;
            ApplyMap(uid, AnatomySlot, key, sprite, layer, null, helpers, map);
        }
        TrimCache();
    }
}
