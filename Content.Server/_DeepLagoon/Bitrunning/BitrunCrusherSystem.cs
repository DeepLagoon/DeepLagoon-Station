using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Mobs.Components;
using Content.Shared.Weapons.Marker;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunCrusherComponent : Component
{
    [DataField] public float DetonationDamage = 50;
    [DataField] public float BackstabBonus = 30;
}

/// <summary>kinetic_crusher.dm: wielding, source mark damage and BOMB-armoured backstabs.</summary>
public sealed class BitrunCrusherSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunCrusherComponent, AttemptMeleeEvent>(OnAttempt);
        SubscribeLocalEvent<BitrunCrusherComponent, GunShotEvent>((Entity<BitrunCrusherComponent> ent, ref GunShotEvent args) =>
        {
            if (!TryComp<MeleeWeaponComponent>(ent, out var melee)) return;
            melee.NextAttack = _timing.CurTime + TimeSpan.FromSeconds(0.8);
            Dirty(ent, melee);
        }, after: [typeof(SharedMeleeWeaponSystem)]);
        SubscribeLocalEvent<MobStateComponent, AttackedEvent>(OnMarkedAttack,
            before: new[] { typeof(SharedDamageMarkerSystem) });
    }

    private void OnAttempt(Entity<BitrunCrusherComponent> ent, ref AttemptMeleeEvent args)
    {
        if (!TryComp<WieldableComponent>(ent, out var wieldable) || !wieldable.Wielded)
            args.Cancelled = true;
    }

    private void OnMarkedAttack(Entity<MobStateComponent> target, ref AttackedEvent args)
    {
        if (!TryComp<BitrunCrusherComponent>(args.Used, out var crusher) ||
            !TryComp<DamageMarkerComponent>(target, out var mark) || mark.EndTime <= _timing.CurTime ||
            !TryComp<WieldableComponent>(args.Used, out var wieldable) || !wieldable.Wielded)
            return;

        var attacker = _transform.GetMapCoordinates(args.User);
        var victim = _transform.GetMapCoordinates(target);
        if (attacker.MapId != victim.MapId)
            return;
        var behind = Vector2.Dot(attacker.Position - victim.Position,
            _transform.GetWorldRotation(target).ToWorldVec()) < 0;
        var amount = crusher.DetonationDamage + (behind ? crusher.BackstabBonus : 0);
        // Source detonation uses BOMB protection; the blade's ordinary melee
        // damage still goes through native melee armour separately.
        var resistance = new GetExplosionResistanceEvent("Default");
        RaiseLocalEvent(target, ref resistance);
        RemComp<DamageMarkerComponent>(target);
        _damage.TryChangeDamage(target, new DamageSpecifier { DamageDict = { ["Blunt"] = amount * resistance.DamageCoefficient } },
            true, origin: args.User);
    }
}
