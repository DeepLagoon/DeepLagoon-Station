using System.Numerics;
using Content.Shared._DeepLagoon.Weapons;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Projectiles;

namespace Content.Server._DeepLagoon.Weapons;

public sealed class LaserProjectileWindowSystem : EntitySystem
{
    [Dependency] private LaserWindowSystem _windows = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LaserProjectileWindowComponent, ProjectileHitEvent>(OnHit,
            before: new[] { typeof(StaminaSystem) });
    }

    public void Track(EntityUid uid)
    {
        if (!TryComp<LaserProjectileWindowComponent>(uid, out var glass))
            return;
        glass.Previous = _transform.GetMapCoordinates(uid);
        glass.Crossed.Clear();
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<LaserProjectileWindowComponent, ProjectileComponent>();
        while (query.MoveNext(out var uid, out var glass, out var projectile))
        {
            if (projectile.Weapon != null && !projectile.ProjectileSpent)
                CrossWindows(uid, glass, projectile);
        }
    }

    private void OnHit(EntityUid uid, LaserProjectileWindowComponent glass, ref ProjectileHitEvent args)
    {
        if (TryComp<ProjectileComponent>(uid, out var projectile))
        {
            var transmission = CrossWindows(uid, glass, projectile, args.Target);
            // ProjectileHitEvent was constructed before this callback updated the projectile.
            args.Damage *= transmission;
        }
    }

    private float CrossWindows(EntityUid uid, LaserProjectileWindowComponent glass,
        ProjectileComponent projectile, EntityUid? target = null)
    {
        var position = _transform.GetMapCoordinates(uid);
        var previous = glass.Previous;
        glass.Previous = position;
        if (previous == null || previous.Value.MapId != position.MapId)
            return 1f;

        var delta = position.Position - previous.Value.Position;
        var distance = delta.Length();
        if (distance <= 0.001f)
            return 1f;
        var transmission = _windows.Transmission(previous.Value, delta / distance, distance, target, glass.Crossed);
        projectile.Damage *= transmission;
        if (TryComp<StaminaDamageOnCollideComponent>(uid, out var stamina))
            stamina.Damage *= transmission;
        return transmission;
    }
}
