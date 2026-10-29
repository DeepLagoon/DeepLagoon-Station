using Content.Server._NF.Salvage;
using Content.Server.NPC.HTN;
using Content.Server.Salvage.Expeditions;
using Content.Shared.NPC.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.Salvage;

/// <summary>
/// Tags native expedition NPCs, including bosses, turrets and deferred spawner children.
/// Membership follows the entity after it boards a shuttle; arriving NPCs retain their own relationships.
/// </summary>
public sealed class SalvageExpeditionHostilitySystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NpcFactionMemberComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<NpcFactionMemberComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);
        if (!HasComp<HTNComponent>(ent) ||
            xform.MapUid is not { } map ||
            !HasComp<SalvageExpeditionComponent>(map) ||
            (xform.GridUid != map && !HasComp<NFSalvageMobRestrictionsComponent>(ent)))
            return;

        EnsureComp<NpcForcedHostilityComponent>(ent).Group = map;

        if (!TryComp<NFSalvageMobRestrictionsComponent>(ent, out var restrictions))
            return;

        // Boarding a shuttle must not automatically make an expedition boss unable to attack.
        // Copy the registries so ordinary NPCs and prototype data retain their restrictions.
        restrictions.AddComponentsLeaveGrid = new ComponentRegistry(restrictions.AddComponentsLeaveGrid);
        restrictions.AddComponentsLeaveGrid.Remove("Pacified");
        restrictions.RemoveComponentsReturnGrid = new ComponentRegistry(restrictions.RemoveComponentsReturnGrid);
        restrictions.RemoveComponentsReturnGrid.Remove("Pacified");
    }
}
