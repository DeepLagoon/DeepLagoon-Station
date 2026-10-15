using Content.Shared.Explosion.Components;
using Content.Shared.Explosion.EntitySystems;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Spreader;
using Content.Shared.Chemistry.Components;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.Explosion.EntitySystems;

/// <summary>
/// Handles creating smoke when <see cref="SmokeOnTriggerComponent"/> is triggered.
/// </summary>
public sealed partial class SmokeOnTriggerSystem : SharedSmokeOnTriggerSystem
{
    private SharedMapSystem _mapMan => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SmokeSystem _smoke = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private SpreaderSystem _spreader = default!;
    [Dependency] private TurfSystem _turf = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SmokeOnTriggerComponent, TriggerEvent>(OnTrigger);
    }

    private void OnTrigger(EntityUid uid, SmokeOnTriggerComponent comp, TriggerEvent args)
    {
        var xform = Transform(uid);
        var mapCoords = _transform.GetMapCoordinates(uid, xform);
        if (!_turf.TryGetTileRef(xform.Coordinates, out var tileRef) || tileRef.Value.Tile.IsEmpty)
            return;

        if (_spreader.RequiresFloorToSpread(comp.SmokePrototype.ToString()) && _turf.IsSpace(tileRef.Value))
            return;

        var coords = _map.MapToGrid(xform.GridUid!.Value, mapCoords);
        var ent = Spawn(comp.SmokePrototype, coords.SnapToGrid());
        if (!TryComp<SmokeComponent>(ent, out var smoke))
        {
            Log.Error($"Smoke prototype {comp.SmokePrototype} was missing SmokeComponent");
            Del(ent);
            return;
        }

        _smoke.StartSmoke(ent, comp.Solution, comp.Duration, comp.SpreadAmount, smoke);
    }
}
