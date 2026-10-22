using Content.Shared.Abilities.Psionics;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory.Events;
using Content.Shared.Psionics;
using Robust.Shared.Prototypes;

namespace Content.Server.Abilities.Psionics;

/// <summary>Equipment grants complete powers, including actions, without removing innate powers on unequip.</summary>
public sealed partial class PsionicEquipmentSystem : EntitySystem
{
    [Dependency] private PsionicAbilitiesSystem _abilities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ClothingGrantPsionicPowerComponent, GotEquippedEvent>(OnEquip);
        SubscribeLocalEvent<ClothingGrantPsionicPowerComponent, GotUnequippedEvent>(OnUnequip);
    }

    private void OnEquip(EntityUid uid, ClothingGrantPsionicPowerComponent component, GotEquippedEvent args)
    {
        if (!TryComp<ClothingComponent>(uid, out var clothing)
            || !clothing.Slots.HasFlag(args.SlotFlags)
            || !_prototypes.TryIndex<PsionicPowerPrototype>(component.Power, out var power))
            return;

        var powers = EnsureComp<EquipmentPsionicPowersComponent>(args.Equipee);
        if (!powers.Sources.TryGetValue(component.Power, out var sources))
        {
            sources = new HashSet<EntityUid>();
            powers.Sources[component.Power] = sources;
            var psionic = EnsureComp<PsionicComponent>(args.Equipee);
            if (!psionic.ActivePowers.Contains(power))
            {
                powers.EquipmentOnly.Add(component.Power);
                _abilities.InitializePsionicPower(args.Equipee, power, psionic);
            }
        }

        sources.Add(uid);
        component.IsActive = true;
    }

    private void OnUnequip(EntityUid uid, ClothingGrantPsionicPowerComponent component, GotUnequippedEvent args)
    {
        if (!component.IsActive)
            return;
        component.IsActive = false;
        if (!TryComp<EquipmentPsionicPowersComponent>(args.Equipee, out var powers)
            || !powers.Sources.TryGetValue(component.Power, out var sources))
            return;

        sources.Remove(uid);
        if (sources.Count > 0)
            return;
        powers.Sources.Remove(component.Power);
        if (powers.EquipmentOnly.Remove(component.Power)
            && _prototypes.TryIndex<PsionicPowerPrototype>(component.Power, out var power))
            _abilities.RemovePsionicPower(args.Equipee, power, forced: true);
    }
}

[RegisterComponent]
public sealed partial class EquipmentPsionicPowersComponent : Component
{
    public readonly Dictionary<string, HashSet<EntityUid>> Sources = new();
    public readonly HashSet<string> EquipmentOnly = new();
}
