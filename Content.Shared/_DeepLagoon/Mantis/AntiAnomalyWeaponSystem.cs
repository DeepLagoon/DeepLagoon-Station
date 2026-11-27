using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Anomaly.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Revenant.Components;
using Content.Shared.Tag;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Shared._DeepLagoon.Mantis;

public sealed class AntiAnomalyWeaponSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly TagSystem _tags = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AntiAnomalyWeaponComponent, MeleeHitEvent>(OnMeleeHit);
    }

    private void OnMeleeHit(Entity<AntiAnomalyWeaponComponent> ent, ref MeleeHitEvent args)
    {

        if (!args.IsHit)
            return;

        foreach (var hit in args.HitEntities)
        {
            if (IsVeilEntity(hit, ent.Comp.VeilFaction))
            {
                args.ModifiersList.Add(ent.Comp.Modifiers);
                return;
            }
        }
    }

    private bool IsVeilEntity(EntityUid uid, string veilFaction) =>
        _tags.HasTag(uid, "NoosphericSignatureAnomalous") ||
        HasComp<AnomalyComponent>(uid) ||
        HasComp<RevenantComponent>(uid) ||
        _factions.IsMember(uid, veilFaction);
}
