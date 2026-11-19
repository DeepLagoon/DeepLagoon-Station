using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Shared.Mobs;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Mobs.Systems;
using System.Linq;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunMegafaunaComponent : Component
{
    [DataField] public bool DeleteOnDeath = true;
}

[RegisterComponent]
public sealed partial class BitrunGuttedComponent : Component;

/// <summary>Megafauna have null atmos requirements and unlimited body-temperature tolerance in WhiteMoon.</summary>
public sealed class BitrunMegafaunaSystem : EntitySystem
{
    [Dependency] private BitrunSystem _bitrun = default!;
    [Dependency] private SharedBodySystem _body = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunMegafaunaComponent, MapInitEvent>(OnInit);
        SubscribeLocalEvent<BitrunMegafaunaComponent, MobStateChangedEvent>(OnDeath);
    }

    public bool Devour(EntityUid boss, EntityUid victim)
    {
        if (TerminatingOrDeleted(victim) || HasComp<BitrunGuttedComponent>(victim)) return false;
        EnsureComp<BitrunGuttedComponent>(victim);
        // Source devour can also be invoked on an unconscious blood-grab victim.
        foreach (var organ in _body.GetBodyOrgans(victim).ToArray())
            if (organ.Component.SlotId is "heart" or "lungs" or "liver") QueueDel(organ.Id);
        _damage.TryChangeDamage(victim, new DamageSpecifier { DamageDict = { ["Blunt"] = 500 } }, true, origin: boss, canSever: false);
        _mobs.ChangeMobState(victim, MobState.Dead);
        if (TryComp<DamageableComponent>(boss, out var health)) BitrunBossGeometry.Heal(_damage, boss, health, 50);
        return true;
    }

    private void OnDeath(Entity<BitrunMegafaunaComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;
        // Capture the location synchronously: a later objective poll must not
        // fall back to a cache marker after a source del_on_death boss is gone.
        _bitrun.RecordMegafaunaDeath(ent);
        if (ent.Comp.DeleteOnDeath)
            QueueDel(ent);
    }

    private void OnInit(Entity<BitrunMegafaunaComponent> ent, ref MapInitEvent args)
    {
        RemComp<BarotraumaComponent>(ent);
        RemComp<RespiratorComponent>(ent);
        RemComp<TemperatureComponent>(ent);
        RemComp<Content.Shared.Damage.Components.StaminaComponent>(ent);
    }
}
