using Content.Shared.Clothing.Components;
using Content.Shared.Foldable;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Shared.Clothing.EntitySystems;

public abstract partial class SharedChameleonClothingSystem
{
    [Dependency] private Robust.Shared.Serialization.Manager.ISerializationManager _appearanceSerialization = default!;

    private void ApplyCopiedAppearance(EntityUid uid, ChameleonClothingComponent component, EntityPrototype prototype)
    {
        if (component.CopiedAppearance is { } copy)
        {
            _clothingSystem.CopyVisuals(uid, copy);
            _itemSystem.CopyVisuals(uid, copy);
        }
        else if (prototype.TryComp<FoldableClothingComponent>(out var fold, Factory))
        {
            if (fold.FoldedEquippedPrefix != null)
                _clothingSystem.SetEquippedPrefix(uid, component.Folded ? fold.FoldedEquippedPrefix : null);
            if (fold.FoldedHeldPrefix != null)
                _itemSystem.SetHeldPrefix(uid, component.Folded ? fold.FoldedHeldPrefix : null);
        }
    }
    private void OnVariantVerb(Entity<ChameleonClothingComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null || HasComp<FoldableComponent>(ent) ||
            !TryGetClothingVariant(ent.Comp, out _, out var foldable))
            return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString(ent.Comp.Folded ? foldable.UnfoldVerbText : foldable.FoldVerbText),
            Act = () => ToggleClothingVariant(ent),
            Priority = 2,
        });
    }

    private bool TryGetClothingVariant(ChameleonClothingComponent component,
        out FoldableClothingComponent clothing, out FoldableComponent foldable)
    {
        clothing = default!;
        foldable = default!;
        if (component.Default is not { } id || !_proto.TryIndex(id, out var prototype) ||
            !prototype.TryComp<FoldableClothingComponent>(out var foundClothing, Factory) ||
            !prototype.TryComp<FoldableComponent>(out var foundFoldable, Factory))
            return false;
        clothing = foundClothing;
        foldable = foundFoldable;
        return true;
    }

    /// <summary>Only change the disguise's visual variant, never its gameplay components or slot permissions.</summary>
    public bool ToggleClothingVariant(Entity<ChameleonClothingComponent> ent)
    {
        if (!TryGetClothingVariant(ent.Comp, out var clothing, out _))
            return false;
        ent.Comp.Folded = !ent.Comp.Folded;
        if (ent.Comp.CopiedAppearance is { } previous)
        {
            // Network states may retain the previous snapshot. Replace it instead of mutating it in place.
            var copy = _appearanceSerialization.CreateCopy(previous, notNullableOverride: true);
            copy.Folded = ent.Comp.Folded;
            if (clothing.FoldedEquippedPrefix != null)
                copy.EquippedPrefix = ent.Comp.Folded ? clothing.FoldedEquippedPrefix : null;
            if (clothing.FoldedHeldPrefix != null)
                copy.HeldPrefix = ent.Comp.Folded ? clothing.FoldedHeldPrefix : null;
            ent.Comp.CopiedAppearance = copy;
        }
        UpdateVisuals(ent, ent.Comp);
        Dirty(ent.Owner, ent.Comp);
        return true;
    }
}