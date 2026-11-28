using System.Linq;
using System.Numerics;
using Content.Server.Cargo.Components;
using Content.Server.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class SectorExpeditionSystem
{
    [Dependency] private ITileDefinitionManager _tileDefinitions = default!;
    private const string RescueHold = "sector-emergency-crew";

    public bool CanInsuredBodyReturnWith(EntityUid body, EntityUid shuttle, EntityUid expeditionMap)
    {
        // Several crews can share one planet. One departing vessel must not
        // consume insurance or take bodies belonging to another active crew.
        if (!_visits.ContainsKey(expeditionMap)) return true;
        return _trips.TryGetValue(shuttle, out var trip) && trip.Bodies.Contains(body);
    }

    private void SecureEmergencyCrew(Trip trip)
    {
        var hold = _containers.EnsureContainer<Container>(trip.Ship, RescueHold);
        foreach (var body in trip.Bodies.Where(Exists))
        {
            // A damaged floor and residual explosion velocity must not leave bodies in hyperspace.
            ResetRescueVelocity(body);
            if (hold.Contains(body)) continue;
            if (_containers.TryGetContainingContainer((body, null, null), out var previous))
                _containers.Remove((body, null, null), previous, force: true, destination: new EntityCoordinates(trip.Ship, Vector2.Zero));
            if (!_containers.Insert((body, null, null, null), hold, force: true))
                throw new InvalidOperationException("Не удалось закрепить тело для аварийной буксировки");
        }
    }

    private void ReleaseEmergencyCrew(Trip trip)
    {
        if (!trip.Wiped || !_containers.TryGetContainer(trip.Ship, RescueHold, out var hold)) return;
        var destination = EmergencyLandingPoint(trip);
        foreach (var body in hold.ContainedEntities.ToArray())
        {
            if (!_containers.Remove((body, null, null), hold, force: true, destination: destination))
                throw new InvalidOperationException("Не удалось выгрузить тело после аварийной буксировки");
            ResetRescueVelocity(body);
        }
        _containers.ShutdownContainer(hold);
    }

    private void ResetRescueVelocity(EntityUid body)
    {
        if (!TryComp<PhysicsComponent>(body, out var physics)) return;
        var system = EntityManager.System<SharedPhysicsSystem>();
        system.SetLinearVelocity(body, Vector2.Zero, body: physics);
        system.SetAngularVelocity(body, 0, body: physics);
    }

    private EntityCoordinates EmergencyLandingPoint(Trip trip)
    {
        if (TryComp<MapGridComponent>(trip.Ship, out var grid))
        {
            var origin = Exists(trip.Console) && Transform(trip.Console).GridUid == trip.Ship ? Transform(trip.Console).LocalPosition : Vector2.Zero;
            var tiles = _maps.GetAllTiles(trip.Ship, grid).Where(t => !t.Tile.IsEmpty && _anchor.TileFree(grid, t.GridIndices,
                (int)Content.Shared.Physics.CollisionGroup.MobLayer, (int)Content.Shared.Physics.CollisionGroup.MobMask))
                .Select(t => _maps.GridTileToLocal(trip.Ship, grid, t.GridIndices)).OrderBy(c => Vector2.DistanceSquared(c.Position, origin)).ToArray();
            if (tiles.Length > 0) return tiles[0];
        }
        return RescueCraft(trip);
    }

    private EntityCoordinates RescueCraft(Trip trip)
    {
        var rescue = _maps.CreateGridEntity(Comp<MapComponent>(trip.HomeMap).MapId);
        var tile = new Tile(_tileDefinitions["Plating"].TileId);
        for (var x = 0; x < 3; x++) for (var y = 0; y < 3; y++) _maps.SetTile(rescue.Owner, rescue.Comp, new EntityCoordinates(rescue.Owner, x, y), tile);
        _transform.SetCoordinates(rescue.Owner, new EntityCoordinates(trip.HomeMap, trip.Home.Position + new Vector2(120, 120)));
        EnsureComp<ShuttleComponent>(rescue.Owner); EnsureComp<CargoSellBlacklistComponent>(rescue.Owner);
        EntityManager.System<MetaDataSystem>().SetEntityName(rescue.Owner, "Спасательный челнок · " + trip.Name);
        return new EntityCoordinates(rescue.Owner, new Vector2(1.5f));
    }
    private void EmergencyReturn(Trip trip)
    {
        if (HasComp<Content.Shared.Shuttles.Components.FTLComponent>(trip.Ship)) return;
        SecureEmergencyCrew(trip);
        EntityManager.System<Content.Server.Shuttles.Systems.DockingSystem>().UndockDocks(trip.Ship);
        trip.Path = trip.HomePath; trip.Phase = Phase.EmergencyHome; trip.Depart = Now; trip.Arrive = Now + (long)(12000 * FlightMultiplier(trip.Ship));
        // Emergency retrieval is a tow: a destroyed or unpowered drive cannot strand an entirely dead crew.
        Jump(trip.Ship, trip.Home, trip.HomeAngle, 12 * FlightMultiplier(trip.Ship));
    }
    private void RescueDestroyedShip(Trip trip)
    {
        // The destroyed hull cannot fly. Retrieve remaining bodies onto a small rescue craft at home.
        if (!Exists(trip.HomeMap) || !_voyages.Ready) return;
        var old = trip.Ship;
        var coordinates = RescueCraft(trip);
        foreach (var body in trip.Bodies.Where(Exists))
        {
            if (_containers.TryGetContainingContainer((body, null, null), out var container)) _containers.Remove((body, null, null), container, force: true, destination: coordinates);
            else _transform.SetCoordinates(body, coordinates);
            ResetRescueVelocity(body);
        }
        MarkVisitFailed(trip);
        if (trip.PlanetMap is { } map && TryComp<Content.Server.Salvage.Expeditions.SalvageExpeditionComponent>(map, out var mission) && mission.Station == old && _visits.TryGetValue(map, out var visits))
        {
            var survivor = visits.FirstOrDefault(v => v.Ship != old && !v.Failed && Exists(v.Ship) && _trips.ContainsKey(v.Ship));
            if (survivor != null)
            {
                mission.Station = survivor.Ship;
                mission.DispatchConsole = _trips[survivor.Ship].Console;
            }
        }
        trip.Wiped = true; _researchClaims.Remove((trip.User, trip.Profile)); _trips.Remove(old); _hireSettlements[trip.Id] = trip;
        Log.Info($"Sector trip {trip.Id}: hull destroyed, {trip.Bodies.Count(Exists)} remaining bodies retrieved at home.");
    }
}
