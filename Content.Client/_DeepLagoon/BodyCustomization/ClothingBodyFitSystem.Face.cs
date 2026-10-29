using System.Linq;
using Content.Client._DeepLagoon.Tattoos;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Nutrition.Components;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.Utility;

namespace Content.Client._DeepLagoon.BodyCustomization;

public sealed partial class ClothingBodyFitSystem
{
    [Dependency] private IResourceCache _faceResources = default!;

    private void ApplyFace(EntityUid uid, EntityUid item, string slot, SpriteComponent sprite,
        ClothingComponent clothing, HashSet<string> keys, HumanoidAppearanceComponent humanoid)
    {
        if (!clothing.FaceFit || HasComp<SmokableComponent>(item) ||
            humanoid.PermanentlyHidden.Contains(HumanoidVisualLayers.Head) ||
            TryComp<MaskComponent>(item, out var mask) && mask.IsToggled)
            return;

        var head = BodyLayers(humanoid, sprite, HumanoidVisualLayers.Head);
        if (head.Count == 0) return;
        var replaced = humanoid.MarkingSet.Markings.Values.SelectMany(markings => markings)
            .Any(marking => marking.Visible && _prototypes.TryIndex<MarkingPrototype>(marking.MarkingId, out var prototype)
                && prototype.IsBodyReplacement && prototype.BodyPart == HumanoidVisualLayers.Head);
        // Marking.Visible is the player's selection, whereas Layer.Visible also reflects equipment.
        // Hidden snouts still define the volume underneath a mask. Hair, ears and horns do not.
        var face = (replaced ? Enumerable.Empty<SpriteComponent.Layer>() : head).Concat(TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypes,
                HumanoidVisualLayers.Head, visibleOnly: true)
            .Concat(TattooBodyPreview.MarkingLayers(humanoid, sprite, _prototypes,
                HumanoidVisualLayers.Snout, visibleOnly: true))
            .Select(index => (SpriteComponent.Layer) sprite[index]))
            .Where(layer => layer.ActualState != null).Distinct().ToList();
        if (sprite.LayerMapTryGet(HumanoidVisualLayers.Snout, out var snout) &&
            sprite[snout] is SpriteComponent.Layer { ActualState: not null } native &&
            !humanoid.PermanentlyHidden.Contains(HumanoidVisualLayers.Snout))
            face.Add(native);

        if (!_faceResources.GetResource<RSIResource>(new ResPath("/Textures/Mobs/Species/Human/parts.rsi"))
                .RSI.TryGetState(humanoid.Sex == Sex.Female ? "head_f" : "head_m", out var reference)) return;
        var part = new Part("face", face, face.Select(layer => layer.ActualState!).ToList(),
            face.Select(_ => reference).ToList());
        ApplyParts(uid, slot, sprite, keys, new List<SpriteComponent.Layer> { head[0] }, new List<Part> { part }, new List<SpriteComponent.Layer>());
    }
}
