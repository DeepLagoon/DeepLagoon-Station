using System.Linq;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.BodyCustomization;

/// <summary>Directional source overlays, ordered behind the body / under or above clothing.</summary>
public sealed partial class BodyCustomizationVisualizerSystem : EntitySystem
{
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private InventorySystem _inventory = default!;
    private readonly Dictionary<EntityUid, List<string>> _layers = new();
    private readonly Dictionary<EntityUid, object[]> _raisedHands = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<HumanoidAppearanceComponent, ComponentRemove>((uid, _, _) => Clear(uid));
    }

    public void Refresh(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        Clear(uid);
        if (!sprite.LayerMapTryGet(HumanoidVisualLayers.Chest, out _)) return;
        var layers = new List<string>();
        _layers[uid] = layers;
        foreach (var feature in BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures))
        {
            if (!feature.Enabled || feature.Part == "womb" || feature.Visibility == 0) continue;
            foreach (var side in BodyCustomizationCatalog.Parts[feature.Part][feature.Shape][feature.Size])
            {
                sprite.LayerMapTryGet(HumanoidVisualLayers.Chest, out var chest);
                var insertion = chest;
                if (side == "FRONT")
                {
                    // Insert just below the first clothing layer, above all base body parts before it.
                    insertion = sprite.LayerMapTryGet("jumpsuit", out var uniform) ? uniform : chest + 1;
                    if (feature.Visibility == 2)
                        insertion = sprite.LayerMapTryGet("outerClothing", out var suit) ? suit + 1 : insertion;
                    foreach (var previous in layers.Where(k => !k.EndsWith("/BEHIND")
                        && humanoid.BodyFeatures.FirstOrDefault(f => f.Part == k.Split('/')[1])?.Visibility == feature.Visibility))
                        if (sprite.LayerMapTryGet(previous, out var index)) insertion = Math.Max(insertion, index + 1);
                }
                var state = BodyCustomizationCatalog.SpriteState(feature, side, humanoid.GenitalAroused);
                var specifier = new SpriteSpecifier.Rsi(new ResPath($"_DeepLagoon/BodyCustomization/{feature.Part}.rsi"), state);
                var layerIndex = _sprites.AddLayer((uid, sprite), specifier, insertion);
                var key = $"dl-body/{feature.Part}/{side}";
                sprite.LayerMapSet(key, layerIndex);
                sprite.LayerSetColor(layerIndex, feature.MatchSkin ? humanoid.SkinColor : Color.FromHex(feature.Color));
                layers.Add(key);
                if (side == "FRONT" && feature.Part == "breasts" && feature.Shape == "pair"
                    && feature.SecondaryEnabled && BodyCustomizationCatalog.BreastSecondarySizes.Contains(feature.Size))
                {
                    var secondary = new SpriteSpecifier.Rsi(new ResPath("_DeepLagoon/BodyCustomization/breasts.rsi"), $"nipples_pair_{feature.Size}_0_FRONT");
                    var secondaryIndex = _sprites.AddLayer((uid, sprite), secondary, layerIndex + 1);
                    var secondaryKey = "dl-body/breasts/SECONDARY";
                    sprite.LayerMapSet(secondaryKey, secondaryIndex);
                    sprite.LayerSetColor(secondaryIndex, Color.FromHex(feature.SecondaryColor));
                    layers.Add(secondaryKey);
                }
            }
        }
        // PR #4108 also keeps hands in front of exposed anatomy. Preserve native layers.
        if (humanoid.BodyFeatures.Any(f => f.Enabled && f.Visibility == 2 && f.Part != "womb")
            && layers.Count > 0 && sprite.LayerMapTryGet("gloves", out _) && sprite.LayerMapTryGet("shoes", out _))
        {
            var keys = new List<object> { HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand };
            foreach (var list in humanoid.MarkingSet.Markings.Values)
            foreach (var marking in list)
            {
                if (!_prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype)
                    || prototype.BodyPart is not (HumanoidVisualLayers.LHand or HumanoidVisualLayers.RHand)) continue;
                foreach (var spec in prototype.Sprites)
                    if (spec is SpriteSpecifier.Rsi rsi) keys.Add($"{prototype.ID}-{rsi.RsiState}");
            }
            var hands = keys.Distinct()
                .Where(key => sprite.LayerMapTryGet(key, out _))
                .OrderBy(key => { sprite.LayerMapTryGet(key, out var index); return index; }).ToArray();
            _raisedHands[uid] = hands;
            object anchor = layers.OrderBy(key => { sprite.LayerMapTryGet(key, out var index); return index; }).Last();
            foreach (var hand in hands.Append("gloves"))
            { MoveLayer(uid, sprite, hand, anchor, after: true); anchor = hand; }
        }
        // Genitals must remain in front of hands, including species built from limb markings.
        var limbs = new HashSet<HumanoidVisualLayers>
        {
            HumanoidVisualLayers.LArm, HumanoidVisualLayers.RArm,
            HumanoidVisualLayers.LHand, HumanoidVisualLayers.RHand,
        };
        var limbKeys = limbs.Cast<object>().Append("gloves").ToList();
        foreach (var marking in humanoid.MarkingSet.Markings.Values.SelectMany(markings => markings))
        {
            if (!_prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype)
                || !limbs.Contains(prototype.BodyPart)) continue;
            foreach (var spec in prototype.Sprites)
                if (spec is SpriteSpecifier.Rsi rsi) limbKeys.Add($"{prototype.ID}-{rsi.RsiState}");
        }
        var limbAnchor = limbKeys.Where(key => sprite.LayerMapTryGet(key, out _))
            .OrderBy(key => { sprite.LayerMapTryGet(key, out var index); return index; }).LastOrDefault();
        if (limbAnchor != null)
        {
            foreach (var key in layers.Where(key => key.EndsWith("/FRONT")
                && key.Split('/')[1] is "vagina" or "anus" or "testicles" or "penis"))
            {
                sprite.LayerMapTryGet(key, out var index);
                sprite.LayerMapTryGet(limbAnchor, out var anchorIndex);
                if (index <= anchorIndex) MoveLayer(uid, sprite, key, limbAnchor, after: true);
                limbAnchor = key;
            }
        }
        UpdateVisibility(uid, humanoid, sprite);
        EntityManager.System<ClothingBodyFitSystem>().Refresh(uid, humanoid, sprite);
    }

    public void UpdateVisibility(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        if (!_layers.TryGetValue(uid, out var layers)) return;
        var clothed = HasTorsoClothing(uid);
        foreach (var key in layers)
        {
            var part = key.Split('/')[1];
            var feature = humanoid.BodyFeatures.FirstOrDefault(f => f.Part == part);
            // The raw layer keeps the naked size. Under clothing, only the garment draws the
            // capped, compressed silhouette; otherwise the larger skin sprite protrudes past it.
            var visible = feature is { Enabled: true } && feature.Visibility != 0
                && !humanoid.PermanentlyHidden.Contains(HumanoidVisualLayers.Chest)
                && (!clothed || feature.Visibility == 2 && feature.ShowThroughClothing)
                && (feature.Visibility == 2 || !humanoid.HiddenLayers.ContainsKey(HumanoidVisualLayers.Chest));
            if (sprite.LayerMapTryGet(key, out var index)) sprite.LayerSetVisible(index, visible);
        }
    }

    private bool HasTorsoClothing(EntityUid uid)
    {
        if (!TryComp<InventoryComponent>(uid, out var inventory)) return false;
        var slots = _inventory.GetSlotEnumerator((uid, inventory));
        while (slots.NextItem(out var item, out var slot))
            if (slot.Name is "jumpsuit" or "outerClothing" && HasComp<ClothingComponent>(item)) return true;
        return false;
    }

    /// <summary>Ink must draw after every anatomy overlay, including parts drawn above clothing.</summary>
    public int LastLayerIndex(EntityUid uid, SpriteComponent sprite)
    {
        if (!_layers.TryGetValue(uid, out var layers)) return -1;
        var last = -1;
        foreach (var key in layers)
            if (sprite.LayerMapTryGet(key, out var index)) last = Math.Max(last, index);
        return last;
    }

    public int[] LayerIndices(EntityUid uid, SpriteComponent sprite, HumanoidVisualLayers part)
    {
        // Every imported organ belongs to the torso/pelvis, not the head or limbs.
        if (part != HumanoidVisualLayers.Chest || !_layers.TryGetValue(uid, out var layers)) return [];
        return layers.Select(key => sprite.LayerMapTryGet(key, out var index) ? index : -1)
            .Where(index => index >= 0).Order().ToArray();
    }

    private void Clear(EntityUid uid)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite))
        { _layers.Remove(uid); _raisedHands.Remove(uid); return; }
        RestoreLayerOrder(uid, sprite);
        if (!_layers.Remove(uid, out var layers)) return;
        foreach (var key in layers)
            if (sprite.LayerMapTryGet(key, out var index)) _sprites.RemoveLayer((uid, sprite), index);
    }

    public void RestoreLayerOrder(EntityUid uid, SpriteComponent sprite)
    {
        if (_raisedHands.Remove(uid, out var hands))
        {
            MoveLayer(uid, sprite, "gloves", "shoes", after: false);
            foreach (var hand in hands) MoveLayer(uid, sprite, hand, "gloves", after: false);
        }
    }

    private void MoveLayer(EntityUid uid, SpriteComponent sprite, object key, object anchor, bool after)
    {
        if (!sprite.LayerMapTryGet(key, out var index) || !sprite.LayerMapTryGet(anchor, out _)) return;
        if (!_sprites.RemoveLayer((uid, sprite), index, out var layer)) return;
        sprite.LayerMapTryGet(anchor, out var insertion);
        var moved = _sprites.AddLayer((uid, sprite), layer, insertion + (after ? 1 : 0));
        sprite.LayerMapSet(key, moved);
    }

    public override void Shutdown()
    {
        foreach (var uid in _layers.Keys.ToArray()) Clear(uid);
        base.Shutdown();
    }
}
