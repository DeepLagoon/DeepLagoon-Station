using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Modsuits;

[Flags, Serializable, NetSerializable]
public enum ModHardlightParts : uint
{
    None = 0,
    // Keep the bits of supported markings. The old anatomical bits are no longer used.
    Hair = 1 << 6,
    Ears = 1 << 7,
    Snout = 1 << 8,
    Tail = 1 << 10,
    Wings = 1 << 11,
    Antennae = 1 << 12,
    Breasts = 1 << 15,
    Buttocks = 1 << 16,
    All = Hair | Ears | Snout | Tail | Wings | Antennae | Breasts | Buttocks,
}

[Serializable, NetSerializable]
public enum ModHardlightEffect : byte
{
    Base,
    Static,
    Scanline,
    Wave,
    Field,
    Hieroglyphs,
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ModHardlightVisualComponent : Component
{
    [AutoNetworkedField]
    public ModHardlightParts Parts;

    [AutoNetworkedField]
    public Color Color;

    [AutoNetworkedField]
    public ModHardlightEffect Effect;
}

[Serializable, NetSerializable]
public enum ModHardlightKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class ModHardlightState(Color color, ModHardlightEffect effect, ModHardlightParts parts) : BoundUserInterfaceState
{
    public readonly Color Color = color;
    public readonly ModHardlightParts Parts = parts;
    public readonly ModHardlightEffect Effect = effect;
}

[Serializable, NetSerializable]
public sealed class ModHardlightStyleMessage(string color, ModHardlightEffect effect) : BoundUserInterfaceMessage
{
    public readonly string Color = color;
    public readonly ModHardlightEffect Effect = effect;
}

public static class ModHardlightRegions
{
    public static readonly (ModHardlightParts Part, string Id, string Slot, string Icon)[] Options =
    [
        (ModHardlightParts.Tail, "tail", "outerClothing", "tail"),
        (ModHardlightParts.Snout, "snout", "head", "snout"),
        (ModHardlightParts.Ears, "ears", "head", "ears"),
        (ModHardlightParts.Breasts, "breasts", "outerClothing", "breasts"),
        (ModHardlightParts.Buttocks, "buttocks", "outerClothing", "butt"),
        (ModHardlightParts.Antennae, "antennae", "head", "moth_antennae"),
        (ModHardlightParts.Wings, "wings", "outerClothing", "some_wings"),
        (ModHardlightParts.Hair, "hair", "head", "open"),
    ];

    public static readonly (ModHardlightParts Part, string Feature)[] BodyFeatures =
    [
        (ModHardlightParts.Breasts, "breasts"),
        (ModHardlightParts.Buttocks, "butt"),
    ];

    public static ModHardlightParts Present(HumanoidAppearanceComponent humanoid, IPrototypeManager prototypes)
    {
        var present = ModHardlightParts.None;
        if (prototypes.TryIndex(humanoid.Species, out var species) &&
            prototypes.TryIndex<HumanoidSpeciesBaseSpritesPrototype>(species.SpriteSet, out var sprites))
        {
            foreach (var (layer, id) in sprites.Sprites)
            {
                var region = FromBaseLayer(layer);
                if (region == ModHardlightParts.None || humanoid.PermanentlyHidden.Contains(layer))
                    continue;

                var current = humanoid.CustomBaseLayers.TryGetValue(layer, out var custom) ? custom.Id?.Id : id;
                if (current != null && prototypes.TryIndex<HumanoidSpeciesSpriteLayer>(current, out var sprite) &&
                    sprite.BaseSprite != null)
                    present |= region;
            }
        }

        // Equipped clothing hides the normal sprite, but its accessories may still be projected by hardlight.
        foreach (var markings in humanoid.MarkingSet.Markings.Values)
        foreach (var marking in markings)
        {
            if (marking.Visible && prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype) &&
                !humanoid.PermanentlyHidden.Contains(prototype.BodyPart))
                present |= FromMarking(prototype);
        }

        foreach (var feature in BodyCustomizationCatalog.Sanitize(humanoid.BodyFeatures))
        {
            if (!feature.Enabled || feature.Visibility == 0)
                continue;

            foreach (var (region, name) in BodyFeatures)
            {
                if (feature.Part == name)
                    present |= region;
            }
        }

        return present;
    }

    public static ModHardlightParts FromBaseLayer(HumanoidVisualLayers layer) => layer switch
    {
        HumanoidVisualLayers.Hair => ModHardlightParts.Hair,
        HumanoidVisualLayers.HeadSide => ModHardlightParts.Ears,
        HumanoidVisualLayers.Snout => ModHardlightParts.Snout,
        HumanoidVisualLayers.Tail => ModHardlightParts.Tail,
        HumanoidVisualLayers.Wings or HumanoidVisualLayers.LArmExtension or HumanoidVisualLayers.RArmExtension => ModHardlightParts.Wings,
        _ => ModHardlightParts.None,
    };

    public static ModHardlightParts FromMarking(MarkingPrototype prototype)
    {
        if (prototype.FilterType == MarkingEditorType.Antennae)
            return prototype.ID.StartsWith("Moth", StringComparison.Ordinal) && prototype.ID != "MothAntennasNone"
                ? ModHardlightParts.Antennae
                : ModHardlightParts.None;

        if (prototype.FilterType == MarkingEditorType.Wings ||
            prototype.ID.Contains("Wing", StringComparison.OrdinalIgnoreCase) &&
            prototype.BodyPart is HumanoidVisualLayers.Tail or HumanoidVisualLayers.TailExtras)
            return ModHardlightParts.Wings;

        if (prototype.FilterType == MarkingEditorType.Tail &&
            (prototype.ID.Contains("Taur", StringComparison.OrdinalIgnoreCase) ||
             prototype.ID.Contains("Spines", StringComparison.OrdinalIgnoreCase) ||
             prototype.ID.Contains("Xenodorsal", StringComparison.OrdinalIgnoreCase)))
            return ModHardlightParts.None;

        return prototype.FilterType switch
        {
            MarkingEditorType.Ears => ModHardlightParts.Ears,
            MarkingEditorType.Snout => ModHardlightParts.Snout,
            MarkingEditorType.Tail => ModHardlightParts.Tail,
            _ => prototype.BodyPart is HumanoidVisualLayers.HeadTop or HumanoidVisualLayers.HeadSide
                ? ModHardlightParts.None
                : FromBaseLayer(prototype.BodyPart),
        };
    }
}
