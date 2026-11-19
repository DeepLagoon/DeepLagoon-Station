using System.Linq;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Inventory;

namespace Content.Shared.Armor;

public abstract partial class SharedArmorSystem
{
    private static DamageModifierSet PenetrateSourceArmor(DamageModifierSet modifiers, float penetration)
    {
        if (penetration == 0) return modifiers;
        var result = new DamageModifierSet();
        foreach (var (type, coefficient) in modifiers.Coefficients)
            result.Coefficients[type] = Math.Clamp(coefficient + penetration, 0, 1);
        foreach (var (type, reduction) in modifiers.FlatReduction)
            result.FlatReduction[type] = reduction;
        return result;
    }

    [Dependency] private InventorySystem _coveredInventory = default!;

    // WhiteMoon human_defense.dm averages six bodypart regions when no zone is
    // specified, after multiplying the protection of clothing covering each one.
    private static readonly TargetBodyPart[] CoveredRegions =
    {
        TargetBodyPart.Head, TargetBodyPart.Torso,
        TargetBodyPart.LeftArm, TargetBodyPart.RightArm,
        TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg,
    };

    private static readonly TargetBodyPart[] TargetableRegions =
    {
        TargetBodyPart.Head, TargetBodyPart.Torso, TargetBodyPart.Groin,
        TargetBodyPart.LeftArm, TargetBodyPart.LeftHand, TargetBodyPart.RightArm, TargetBodyPart.RightHand,
        TargetBodyPart.LeftLeg, TargetBodyPart.LeftFoot, TargetBodyPart.RightLeg, TargetBodyPart.RightFoot,
    };

    private DamageModifierSet GetCoveredModifiers(EntityUid item, TargetBodyPart? target, out float explosion)
    {
        var pieces = new List<ArmorComponent>();
        var wearer = Transform(item).ParentUid;
        var equipment = _coveredInventory.GetSlotEnumerator(wearer, ~SlotFlags.POCKET);
        while (equipment.NextItem(out var uid))
            if (TryComp<ArmorComponent>(uid, out var armor) && armor.Coverage != null) pieces.Add(armor);
        var regions = target is {} part && part != TargetBodyPart.All
            ? TargetableRegions.Where(region => (region & part) != 0).ToArray()
            : CoveredRegions;
        var modifiers = new DamageModifierSet();
        explosion = 1;
        if (regions.Length == 0 || pieces.Count == 0) return modifiers;
        foreach (var type in pieces.SelectMany(p => p.Modifiers.Coefficients.Keys).Distinct())
        {
            var sum = 0f;
            foreach (var region in regions)
            {
                var coefficient = 1f;
                foreach (var piece in pieces)
                    if ((piece.Coverage!.Value & region) != 0) coefficient *= piece.Modifiers.Coefficients.GetValueOrDefault(type, 1);
                sum += coefficient;
            }
            modifiers.Coefficients[type] = sum / regions.Length;
        }
        explosion = 0;
        foreach (var region in regions)
        {
            var coefficient = 1f;
            foreach (var piece in pieces)
                if ((piece.Coverage!.Value & region) != 0) coefficient *= piece.CoveredExplosionCoefficient;
            explosion += coefficient / regions.Length;
        }
        return modifiers;
    }

    private void OnCoveredExplosionResistance(Entity<ArmorComponent> ent, ref InventoryRelayedEvent<GetExplosionResistanceEvent> args)
    {
        if (ent.Comp.Coverage == null || args.Args.CoveredArmorApplied) return;
        args.Args.CoveredArmorApplied = true;
        GetCoveredModifiers(ent, null, out var explosion);
        args.Args.DamageCoefficient *= explosion;
    }
}
