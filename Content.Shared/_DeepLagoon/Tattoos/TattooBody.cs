using System.Linq;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Tattoos;

public static class TattooBody
{
    public static IEnumerable<MarkingPrototype> BodyMarkings(HumanoidAppearanceComponent humanoid, HumanoidVisualLayers part, IPrototypeManager prototypes)
    {
        foreach (var list in humanoid.MarkingSet.Markings.Values)
        foreach (var marking in list)
            if (marking.Visible && prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype) && prototype.BodyPart == part)
                yield return prototype;
    }

    // BaseLayers is populated by the client visualizer. Resolve prototypes on the server too.
    public static bool Allows(HumanoidAppearanceComponent humanoid, HumanoidVisualLayers part, IPrototypeManager prototypes)
    {
        if (humanoid.PermanentlyHidden.Contains(part)) return false;
        if (humanoid.CustomBaseLayers.TryGetValue(part, out var custom))
            return custom.Id is { } id && prototypes.TryIndex(id, out var layer) && layer.BaseSprite != null && layer.AllowsMarkings;
        if (!prototypes.TryIndex(humanoid.Species, out var species)
            || !prototypes.TryIndex<HumanoidSpeciesBaseSpritesPrototype>(species.SpriteSet, out var sprites)
            || !sprites.Sprites.TryGetValue(part, out var sprite)) return false;
        sprite = HumanoidVisualLayersExtension.GetSexMorph(part, humanoid.Sex, sprite);
        return prototypes.TryIndex<HumanoidSpeciesSpriteLayer>(sprite, out var baseLayer) && baseLayer.AllowsMarkings
            && (baseLayer.BaseSprite != null || species.BodyMarkingAnchor != null && BodyMarkings(humanoid, part, prototypes).Any());
    }
}
