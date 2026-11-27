using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Voyages;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionSystem
{
    private readonly HashSet<(string Sector, int Planet)> _planetLaunching = new();
    private readonly Dictionary<(string Sector, int Planet), EntityUid> _planetMaps = new();
    private readonly Dictionary<EntityUid, Vector2> _landingOrigins = new();
    private readonly Dictionary<EntityUid, Dictionary<EntityUid, Box2>> _landingReservations = new();
    private readonly HashSet<EntityUid> _retainedPlanets = new();

    private bool CanJoinPlanet(EntityUid map) => !TerminatingOrDeleted(map) &&
        TryComp<SalvageExpeditionComponent>(map, out var mission) && (!mission.Completed || _retainedPlanets.Contains(map)) &&
        mission.Stage is (Content.Shared.Salvage.Expeditions.ExpeditionStage.Added or Content.Shared.Salvage.Expeditions.ExpeditionStage.Running) &&
        _visits.TryGetValue(map, out var visits) && visits.Count < 32;

    public bool HasPendingSectorLanding(EntityUid map) => _trips.Values.Any(t => t.PlanetMap == map && t.Phase == Phase.GeneratingPlanet);

    public bool RetainSectorPlanet(EntityUid map)
    {
        var key = _planetMaps.FirstOrDefault(p => p.Value == map).Key;
        if (key.Sector == null || SectorResearchDefinition.Settled(key.Sector) ||
            !_trips.Values.Any(t => t.Sector == key.Sector && ResearchLandingAllowed(t, key.Planet))) return false;
        _retainedPlanets.Add(map); return true;
    }

    public void ForgetRetainedPlanet(EntityUid map)
    {
        _retainedPlanets.Remove(map); _visits.Remove(map); _landingOrigins.Remove(map); _landingReservations.Remove(map);
        _workProof.RemoveWhere(p => p.Map == map);
        foreach (var pair in _planetMaps.Where(p => p.Value == map).ToArray()) _planetMaps.Remove(pair.Key);
    }

    /// <summary>Reserve footprints before FTL starts, including other ships still in hyperspace.</summary>
    public EntityCoordinates PrepareSectorLanding(EntityUid ship, EntityUid map, Vector2 preferred)
    {
        if (!_landingOrigins.TryGetValue(map, out var origin)) _landingOrigins[map] = origin = preferred;
        if (!_landingReservations.TryGetValue(map, out var reservations)) _landingReservations[map] = reservations = new();
        var footprint = Comp<MapGridComponent>(ship).LocalAABB.Enlarged(8f);
        var lookup = EntityManager.System<EntityLookupSystem>();
        bool Free(Vector2 point)
        {
            var bounds = footprint.Translated(point);
            if (reservations.Any(r => r.Key != ship && r.Value.Intersects(bounds))) return false;
            var grids = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (grids.MoveNext(out var uid, out var grid, out var transform))
            {
                if (uid == ship || uid == map || transform.MapUid != map) continue;
                if (_transform.GetWorldMatrix(uid).TransformBox(grid.LocalAABB).Enlarged(8).Intersects(bounds)) return false;
            }
            foreach (var obstacle in lookup.GetEntitiesIntersecting(map, bounds, LookupFlags.Static | LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                if (obstacle == map || obstacle == ship || Transform(obstacle).GridUid == ship) continue;
                if (TryComp<PhysicsComponent>(obstacle, out var physics) && physics.CanCollide &&
                    TryComp<FixturesComponent>(obstacle, out var fixtures) && fixtures.Fixtures.Values.Any(f => f.Hard)) return false;
            }
            reservations[ship] = bounds;
            return true;
        }
        // Prefer the original native landing strip, then nearby ground outside the complex.
        if (Free(origin)) return new(map, origin);
        for (var radius = 24; radius <= 600; radius += 24)
            for (var i = 0; i < 24; i++)
            {
                var point = (origin + new Angle(i * Math.PI / 12).ToVec() * radius).Rounded();
                if (Free(point)) return new(map, point);
            }
        throw new InvalidOperationException("Возле комплекса нет безопасного места для посадки этого шаттла");
    }
    private void MarkVisitFailed(Trip trip)
    {
        foreach (var (map, visits) in _visits)
            for (var i = 0; i < visits.Count; i++)
                if (visits[i].Trip == trip.Id && !_workProof.Contains((map, trip.Id))) visits[i] = visits[i] with { Failed = true };
    }
}
