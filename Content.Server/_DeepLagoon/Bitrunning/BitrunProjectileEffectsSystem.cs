using System.Linq;
using System.Numerics;
using Content.Shared.Damage;
using Content.Server.Destructible;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Projectiles;
using Content.Shared.Sprite;
using Content.Shared.Stunnable;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Bitrunning;

[RegisterComponent]
public sealed partial class BitrunProjectileEffectsComponent : Component
{
    [DataField] public float Paralyze;
    [DataField] public float Knockdown;
    [DataField] public float Shrink;
}

[RegisterComponent]
public sealed partial class BitrunShrunkComponent : Component
{
    [DataField] public TimeSpan Until;
    [DataField] public Vector2 Scale;
    [DataField] public bool CanCollide;
    [DataField] public bool MobCollision;
    [DataField] public float PushStrength;
    [DataField] public bool Humanoid;
    public bool Restoring;
}

/// <summary>Timed source effects, including restoration after shrinking.</summary>
public sealed class BitrunProjectileEffectsSystem : EntitySystem
{
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedScaleVisualsSystem _scale = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private MovementSpeedModifierSystem _speed = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BitrunProjectileEffectsComponent, ProjectileHitEvent>(OnHit);
        SubscribeLocalEvent<BitrunShrunkComponent, RefreshMovementSpeedModifiersEvent>((Entity<BitrunShrunkComponent> ent, ref RefreshMovementSpeedModifiersEvent args) =>
        {
            if (!ent.Comp.Restoring) args.ModifySpeed(0.25f, 0.25f);
        });
        SubscribeLocalEvent<BitrunShrunkComponent, DamageModifyEvent>((Entity<BitrunShrunkComponent> ent, ref DamageModifyEvent args) =>
        {
            if (ent.Comp.Humanoid && !ent.Comp.Restoring) args.Damage *= 2;
        });
        SubscribeLocalEvent<BitrunShrunkComponent, ComponentRemove>(OnRestore);
    }

    private void OnHit(Entity<BitrunProjectileEffectsComponent> projectile, ref ProjectileHitEvent args)
    {
        if (projectile.Comp.Paralyze > 0)
            _stun.TryParalyze(args.Target, TimeSpan.FromSeconds(projectile.Comp.Paralyze), true);
        if (projectile.Comp.Knockdown > 0)
            _stun.TryKnockdown(args.Target, TimeSpan.FromSeconds(projectile.Comp.Knockdown), true);
        if (projectile.Comp.Shrink > 0)
            Shrink(args.Target, projectile.Comp.Shrink);
    }

    public bool Shrink(EntityUid target, float seconds)
    {
        if (!HasComp<PhysicsComponent>(target) ||
            TryComp<DestructibleComponent>(target, out var destructible) && destructible.Thresholds.Count == 0)
            return false;
        if (TryComp<BitrunShrunkComponent>(target, out var current))
        {
            current.Until = _timing.CurTime + TimeSpan.FromSeconds(seconds);
            return true;
        }
        var effect = AddComp<BitrunShrunkComponent>(target);
        effect.Until = _timing.CurTime + TimeSpan.FromSeconds(seconds);
        effect.Scale = _scale.GetSpriteScale(target);
        effect.CanCollide = Comp<PhysicsComponent>(target).CanCollide;
        effect.Humanoid = HasComp<HumanoidAppearanceComponent>(target);
        if (TryComp<MobCollisionComponent>(target, out var collision))
        {
            effect.MobCollision = true;
            effect.PushStrength = collision.Strength;
            RemComp<MobCollisionComponent>(target);
        }
        if (!HasComp<MobStateComponent>(target))
            _physics.SetCanCollide(target, false);
        _scale.SetSpriteScale(target, effect.Scale * 0.5f);
        if (effect.Humanoid)
        {
            var names = _inventory.TryGetSlots(target, out var slots)
                ? slots.Select(slot => slot.Name).ToArray() : [];
            foreach (var name in names)
                _inventory.TryUnequip(target, name, force: true);
            foreach (var held in _hands.EnumerateHeld(target).ToArray())
                _hands.TryDrop(target, held, checkActionBlocker: false);
        }
        _speed.RefreshMovementSpeedModifiers(target);
        return true;
    }

    private void OnRestore(Entity<BitrunShrunkComponent> target, ref ComponentRemove args)
    {
        if (TerminatingOrDeleted(target))
            return;
        target.Comp.Restoring = true;
        _scale.SetSpriteScale(target, target.Comp.Scale);
        if (!HasComp<MobStateComponent>(target))
            _physics.SetCanCollide(target, target.Comp.CanCollide);
        if (target.Comp.MobCollision)
            EnsureComp<MobCollisionComponent>(target).Strength = target.Comp.PushStrength;
        _speed.RefreshMovementSpeedModifiers(target);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<BitrunShrunkComponent>();
        while (query.MoveNext(out var uid, out var effect))
            if (_timing.CurTime >= effect.Until)
                RemCompDeferred<BitrunShrunkComponent>(uid);
    }
}
