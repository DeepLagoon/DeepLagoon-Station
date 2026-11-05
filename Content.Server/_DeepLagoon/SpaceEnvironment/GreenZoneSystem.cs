using System.Numerics;
using Content.Server._Mono.FireControl;
using Content.Server._Mono.SpaceArtillery.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.ShipGuns;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Projectiles;
using Content.Shared.Station.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[RegisterComponent]
public sealed partial class GreenZoneComponent : Component
{
    [DataField]
    public float Radius = 2000f;
}

public sealed class GreenZoneSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<GunComponent, AttemptShootEvent>(OnAttemptShoot);
        SubscribeLocalEvent<GunComponent, GetHitscanRangeEvent>(OnHitscanRange);
    }

    private bool IsShipGun(EntityUid? uid) =>
        HasComp<ShipGunClassComponent>(uid) || HasComp<FireControllableComponent>(uid) ||
        HasComp<SpaceArtilleryComponent>(uid);

    private void OnAttemptShoot(Entity<GunComponent> gun, ref AttemptShootEvent args)
    {
        if (args.Cancelled || !IsShipGun(gun) || !Contains(_transform.GetMapCoordinates(gun.Owner)))
            return;

        args.Cancelled = true;
        args.Message = Loc.GetString("dl-green-zone-gun-blocked");
    }

    private void OnHitscanRange(Entity<GunComponent> gun, ref GetHitscanRangeEvent args)
    {
        if (IsShipGun(gun) && TryFindBoundary(args.Start, args.Direction * args.Range, out var fraction))
            args.Range *= fraction;
    }

    public bool IsShipProjectile(EntityUid uid, ProjectileComponent component) =>
        IsShipGun(component.Weapon) || HasComp<ShipWeaponProjectileComponent>(uid);

    public bool Contains(MapCoordinates point) => TryFindBoundary(point, Vector2.Zero, out _);

    public bool TryFindBoundary(MapCoordinates start, Vector2 movement, out float fraction)
    {
        fraction = 1f;
        var found = false;
        var zones = EntityQueryEnumerator<GreenZoneComponent, MapGridComponent, TransformComponent>();
        while (zones.MoveNext(out var uid, out var zone, out var grid, out var transform))
        {
            if (start.MapId == MapId.Nullspace || start.MapId != transform.MapID || zone.Radius <= 0f ||
                TerminatingOrDeleted(uid))
                continue;

            var center = _transform.ToMapCoordinates(new EntityCoordinates(uid, grid.LocalAABB.Center));
            if (!TryIntersectCircle(start.Position - center.Position, movement, zone.Radius, out var hit) || hit > fraction)
                continue;

            fraction = hit;
            found = true;
        }

        return found;
    }

    internal static bool TryIntersectCircle(Vector2 offset, Vector2 delta, float radius, out float fraction)
    {
        fraction = 0f;
        var outside = offset.LengthSquared() - radius * radius;
        if (outside <= 0f)
            return true;

        var speedSquared = delta.LengthSquared();
        var dot = Vector2.Dot(offset, delta);
        var discriminant = dot * dot - speedSquared * outside;
        if (speedSquared <= 0f || dot >= 0f || discriminant < 0f)
            return false;

        fraction = outside / (-dot + MathF.Sqrt(discriminant));
        return fraction is >= 0f and <= 1f;
    }

    public bool SuppressesFire(EntityUid entity)
    {
        if (!TryComp<TransformComponent>(entity, out var transform))
            return false;

        return SuppressesFire(_transform.GetMapCoordinates(transform), transform.GridUid);
    }

    public bool SuppressesFire(EntityCoordinates coordinates, EntityUid grid) =>
        SuppressesFire(_transform.ToMapCoordinates(coordinates), grid);

    private bool SuppressesFire(MapCoordinates position, EntityUid? grid)
    {
        // POIs and station grids also have ShuttleComponent, so that marker alone doesn't identify a vessel.
        if (!HasComp<GreenZoneComponent>(grid) &&
            (HasComp<VesselComponent>(grid) || HasComp<ShipOwnershipComponent>(grid) ||
             !HasComp<StationMemberComponent>(grid) && TryComp<ShuttleComponent>(grid, out var shuttle) &&
             shuttle.AngularThrusters.Count > 0))
            return false;

        return Contains(position);
    }
}
