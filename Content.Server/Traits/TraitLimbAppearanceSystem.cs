using System.Linq;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.Traits;

/// <summary>Preserves a character's limb appearance when a trait installs a prosthesis.</summary>
public sealed class TraitLimbAppearanceSystem : EntitySystem
{
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public Dictionary<HumanoidVisualLayers, BodyPartAppearanceComponent> CaptureAppearance(EntityUid original)
    {
        var result = new Dictionary<HumanoidVisualLayers, BodyPartAppearanceComponent>();
        foreach (var (_, part) in _body.GetBodyPartChildren(original))
        {
            if (part.ToHumanoidLayers() is not { } layer
                || !TryComp(part.Body, out HumanoidAppearanceComponent? humanoid))
                continue;

            var appearance = new BodyPartAppearanceComponent { Type = layer };
            var species = _prototypes.Index(humanoid.Species);
            var sprites = _prototypes.Index<HumanoidSpeciesBaseSpritesPrototype>(species.SpriteSet);
            if (sprites.Sprites.TryGetValue(layer, out var sprite))
                appearance.ID = HumanoidVisualLayersExtension.GetSexMorph(layer, humanoid.Sex, sprite);
            appearance.Color = humanoid.SkinColor;
            if (humanoid.CustomBaseLayers.TryGetValue(layer, out var custom))
            {
                if (custom.Id != null)
                    appearance.ID = custom.Id;
                appearance.Color = custom.Color;
            }

            foreach (var sublayer in HumanoidVisualLayersExtension.Sublayers(layer))
            {
                var category = MarkingCategoriesConversion.FromHumanoidVisualLayers(sublayer);
                if (humanoid.MarkingSet.Markings.TryGetValue(category, out var markings))
                    appearance.Markings[sublayer] = markings.Select(m => new Marking(m)).ToList();
            }
            result[layer] = appearance;
        }
        return result;
    }

    // Child hands and feet are generated during attachment, so restore after AttachPart completes.
    public void ApplyAppearance(EntityUid replacement,
        Dictionary<HumanoidVisualLayers, BodyPartAppearanceComponent> appearances)
    {
        foreach (var (id, part) in _body.GetBodyPartChildren(replacement))
        {
            if (part.ToHumanoidLayers() is not { } layer
                || !appearances.TryGetValue(layer, out var copy)
                || part.Body is not { } body
                || !TryComp(body, out HumanoidAppearanceComponent? humanoid))
                continue;

            var target = EnsureComp<BodyPartAppearanceComponent>(id);
            target.Type = copy.Type;
            target.ID = copy.ID;
            target.Color = copy.Color;
            target.EyeColor = copy.EyeColor;
            target.Markings = copy.Markings;
            part.BaseLayerId = copy.ID?.Id;
            part.Species = humanoid.Species.Id;
            _humanoid.SetBaseLayerId(body, layer, copy.ID?.Id, humanoid: humanoid);
            _humanoid.SetBaseLayerColor(body, layer, copy.Color, humanoid: humanoid);
            foreach (var (sublayer, markings) in copy.Markings)
            {
                var category = MarkingCategoriesConversion.FromHumanoidVisualLayers(sublayer);
                humanoid.MarkingSet.Markings[category] = markings.Select(m => new Marking(m)).ToList();
                _humanoid.SetLayerVisibility((body, humanoid), sublayer, true, null);
            }
            Dirty(body, humanoid);
            Dirty(id, target);
            Dirty(id, part);
        }
    }
}
