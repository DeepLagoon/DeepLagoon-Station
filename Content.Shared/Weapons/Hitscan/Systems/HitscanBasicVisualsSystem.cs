
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

        // Переводим начало луча в координаты карты,
        // независимо от положения и поворота сетки.
        var mapCoords =
            _transform.ToMapCoordinates(args.FromCoordinates);

        var origin =
            _transform.ToCoordinates(mapCoords);

        // Передаём один набор параметров выстрела.
        // Клиент сам создаст все визуальные слои.
        var ev = new SharedGunSystem.HitscanEvent
        {
            Origin = GetNetCoordinates(origin),
            ShotAngle = args.ShotDirection.ToAngle(),
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
