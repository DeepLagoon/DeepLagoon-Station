using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Hands;
using Content.Shared.StatusEffect;

namespace Content.Shared._DeepLagoon.Mantis;

public sealed class InsulativeShieldSystem : EntitySystem
{
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<InsulativeShieldComponent, GotEquippedHandEvent>(OnEquipped);
        SubscribeLocalEvent<InsulativeShieldComponent, GotUnequippedHandEvent>(OnUnequipped);
    }

    private void OnEquipped(Entity<InsulativeShieldComponent> ent, ref GotEquippedHandEvent args)
    {
        ent.Comp.Active = true;

        var insulation = EnsureComp<PsionicInsulationComponent>(args.User);
        insulation.Passthrough = ent.Comp.Passthrough;
    }

    private void OnUnequipped(Entity<InsulativeShieldComponent> ent, ref GotUnequippedHandEvent args)
    {
        if (!ent.Comp.Active)
            return;

        ent.Comp.Active = false;

        if (HasOtherActiveShield(args.User, ent.Owner))
            return;

        if (!_statusEffects.HasStatusEffect(args.User, "PsionicallyInsulated"))
            RemComp<PsionicInsulationComponent>(args.User);
    }

    private bool HasOtherActiveShield(EntityUid user, EntityUid dropped)
    {
        var query = EntityQueryEnumerator<InsulativeShieldComponent>();

        while (query.MoveNext(out var uid, out var shield))
        {
            if (uid == dropped || !shield.Active)
                continue;

            if (Transform(uid).ParentUid == user)
                return true;
        }

        return false;
    }
}
