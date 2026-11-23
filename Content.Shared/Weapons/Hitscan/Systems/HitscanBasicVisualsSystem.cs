using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Shared.Weapons.Hitscan.Systems;

public sealed partial class HitscanBasicVisualsSystem : EntitySystem
{
    [Dependency]
    private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<
            HitscanBasicVisualsComponent,
            HitscanRaycastFiredEvent>(
            OnHitscanHit,
            before: [typeof(HitscanReflectSystem)]);
    }

    private void OnHitscanHit(
        Entity<HitscanBasicVisualsComponent> hitscan,
        ref HitscanRaycastFiredEvent args)
    {
        if (args.DistanceTried <= 0f)
            return;

        // The raycast itself is already correct in map coordinates.
        // Only the visual effect's reference frame is changed here.
        var mapCoords = _transform.ToMapCoordinates(args.FromCoordinates);
        var origin = _transform.ToCoordinates(mapCoords);
        var shotAngle = args.ShotDirection.ToAngle();

        // Keep the visual muzzle at the fired position on the moving shuttle.
        // Sending a map-parented origin here makes the laser lag behind the
        // shuttle between the server event and its client-side display.
        // Do not parent it to the actor, since actor movement after firing
        // must not drag the beam around.
        if (_transform.GetGrid(args.FromCoordinates) is { } gridUid &&
            TryComp(gridUid, out TransformComponent? gridXform))
        {
            origin = _transform.WithEntityId(args.FromCoordinates, gridUid);

            // The client creates HitscanEffectRoot with a LOCAL rotation.
            // A world-space angle on a rotated grid would aim incorrectly.
            shotAngle -= _transform.GetWorldRotation(gridXform);
        }

        var ev = new SharedGunSystem.HitscanEvent
        {
            Origin = GetNetCoordinates(origin),
            ShotAngle = shotAngle,
            Distance = args.DistanceTried,

            MuzzleFlash = hitscan.Comp.MuzzleFlash,
            TravelFlash = hitscan.Comp.TravelFlash,
            ImpactFlash = hitscan.Comp.ImpactFlash,
        };

        RaiseNetworkEvent(
            ev,
            Filter.Pvs(origin, entityMan: EntityManager));
    }
}
