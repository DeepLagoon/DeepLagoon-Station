using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server._NF.StationEvents.Components;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipGuns;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.GameTicking.Components;
using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Content.Shared.Maps;
using Content.Shared.Shuttles.Components;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class GreenZoneTests
{
    [TestCase(100f, 0f, 2000f, false, true)]
    [TestCase(400f, 0f, 2000f, false, true)]
    [TestCase(100f, -9f, 10f, false, true)]
    [TestCase(100f, 2500f, 2000f, false, false)]
    [TestCase(100f, 0f, 0f, false, false)]
    [TestCase(100f, 0f, 2000f, true, false)]
    public async Task GreenZonePreventsHullAndTileImpactDamage(
        float speed, float zoneX, float radius, bool otherMap, bool protectedImpact)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var physics = entities.System<SharedPhysicsSystem>();
            for (var x = 0; x < 12; x++)
            for (var y = 0; y < 6; y++)
                maps.SetTile(map.Grid, new Vector2i(x, y), new Tile(1));
            entities.System<ShuttleSystem>().Disable(map.Grid);
            entities.AddComponent<VesselComponent>(map.Grid);

            var hull = new List<EntityUid>();
            for (var y = 0; y < 6; y++)
                hull.Add(entities.SpawnEntity("WallShuttle", new EntityCoordinates(map.Grid, 0.5f, y + 0.5f)));

            // An unowned grid must receive the same protection as a registered vessel.
            var debris = maps.CreateGridEntity(map.MapId);
            maps.SetTile(debris, Vector2i.Zero, new Tile(1));
            transforms.SetLocalPosition(debris, new Vector2(-2, 2));
            hull.Add(entities.SpawnEntity("WallShuttle", new EntityCoordinates(debris, 0.5f, 0.5f)));
            foreach (var wall in hull)
                entities.System<DamageableSystem>().SetDamageModifierSetId(wall, null);
            entities.GetComponent<ShuttleComponent>(debris).Enabled = false;

            var zoneMapId = map.MapId;
            if (otherMap)
                maps.CreateMap(out zoneMapId);
            var zone = maps.CreateGridEntity(zoneMapId);
            maps.SetTile(zone, Vector2i.Zero, new Tile(1));
            // Keep the zone's source grid away from the two colliding hulls.
            transforms.SetLocalPosition(zone, new Vector2(zoneX - 0.5f, radius == 10f ? 2f : 100f));
            entities.AddComponent<GreenZoneComponent>(zone).Radius = radius;

            // The small-zone case deliberately has the large ship's center outside:
            // protection follows the actual hull contact rather than the grid origin.
            var zones = entities.System<GreenZoneSystem>();
            Assert.That(zones.Contains(new MapCoordinates(new Vector2(0f, 2.5f), map.MapId)),
                Is.EqualTo(protectedImpact));
            physics.SetLinearVelocity(debris, new Vector2(speed, 0));
            physics.Update(1f / 30);
            physics.Update(1f / 30);

            bool Damaged(EntityUid wall) => !entities.EntityExists(wall) ||
                entities.GetComponent<DamageableComponent>(wall).TotalDamage > 0;
            if (protectedImpact)
            {
                Assert.That(hull.Any(Damaged), Is.False, "Neither hull may take impact damage inside the green zone");
                Assert.That(maps.GetAllTiles(map.Grid, map.Grid.Comp).Count(), Is.EqualTo(72));
                Assert.That(maps.GetAllTiles(debris, debris.Comp).Count(), Is.EqualTo(1));
            }
            else
            {
                Assert.That(hull.Any(Damaged), Is.True, "Impacts outside an active green zone must still damage hulls");
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestPrototypes]
    private const string Prototypes = """
        - type: dungeonConfig
          id: GreenZoneTestDungeon
          layers:
          - !type:NoiseDunGen
            tileCap: 4
            capStd: 0
            iterations: 1
            layers:
            - tile: Plating
              threshold: -2
              noise: {}

        - type: entity
          id: GreenZoneTestDungeonEvent
          parent: BaseGameRule
          components:
          - type: BluespaceErrorRule
            extendIfPopulated: false
            groups:
              dungeon: !type:DungeonSpawnGroup
                minimumDistance: 1000
                maximumDistance: 2000
                protos: [GreenZoneTestDungeon]
        """;

    [Test]
    public async Task BluespaceEventsSpawnAtLeastFourKilometersFromGreenZoneCenter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true, Dirty = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var ticker = server.System<GameTicker>();
        var maps = server.System<SharedMapSystem>();
        var transforms = server.System<SharedTransformSystem>();
        var eventGrids = new List<Entity<TransformComponent>>();
        EntityUid dungeonRule = default;
        EntityUid dungeonGrid = default;
        var center = new Vector2(1500f, 300f);

        await server.WaitAssertion(() =>
        {
            ticker.SetGamePreset("sandbox");
            ticker.StartRound(force: true);
            var zone = maps.CreateGridEntity(ticker.DefaultMap);
            transforms.SetCoordinates(zone.Owner, transforms.ToCoordinates(new MapCoordinates(center, ticker.DefaultMap)));
            entities.AddComponent<GreenZoneComponent>(zone);

            foreach (var (id, count) in new[] { ("BluespaceCacheError", 1), ("BluespaceSalvage", 3), ("GreenZoneTestDungeonEvent", 1) })
            {
                var rule = ticker.AddGameRule(id);
                entities.GetComponent<GameRuleComponent>(rule).Delay = null;
                Assert.That(ticker.StartGameRule(rule), Is.True, id);
                var grids = entities.GetComponent<BluespaceErrorRuleComponent>(rule).GridsUid;
                Assert.That(grids, Has.Count.EqualTo(count), id);
                if (id == "GreenZoneTestDungeonEvent")
                {
                    dungeonRule = rule;
                    dungeonGrid = grids.Single();
                }
                foreach (var grid in grids)
                    eventGrids.Add((grid, entities.GetComponent<TransformComponent>(grid)));
            }
        });

        await PoolManager.WaitUntil(server, () => eventGrids.All(grid => grid.Comp.MapID == ticker.DefaultMap), tickStep: 10);

        await server.WaitAssertion(() =>
        {
            foreach (var (uid, _) in eventGrids)
            {
                var grid = entities.GetComponent<MapGridComponent>(uid);
                var position = transforms.ToMapCoordinates(new EntityCoordinates(uid, grid.LocalAABB.Center));
                Assert.That(position.MapId, Is.EqualTo(ticker.DefaultMap));
                Assert.That(Vector2.Distance(position.Position, center), Is.GreaterThanOrEqualTo(4000f));
            }
            Assert.That(ticker.EndGameRule(dungeonRule), Is.True);
            Assert.That(entities.Deleted(dungeonGrid), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ZetaNodeZoneAppearsInNavigationAndFtlStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var zetaNode = pair.Server.ResolveDependency<IPrototypeManager>().Index(new ProtoId<GameMapPrototype>("ZetaNode"));
            entities.System<StationSystem>().InitializeNewStation(zetaNode.Stations["ZetaNode"], [map.Grid]);
            Assert.That(entities.HasComponent<GreenZoneComponent>(map.Grid), Is.True);

            var radar = entities.SpawnEntity(null, map.GridCoords);
            entities.AddComponent<RadarConsoleComponent>(radar);
            var consoles = entities.System<ShuttleConsoleSystem>();
            var nav = consoles.GetNavState(radar, new());
            Assert.That(nav.GreenZones, Has.Count.EqualTo(1));
            var zone = nav.GreenZones[0];
            Assert.Multiple(() =>
            {
                Assert.That(zone.IsGreenZone, Is.True);
                Assert.That(zone.Range, Is.EqualTo(2000f));
                Assert.That(zone.Name, Is.Not.Empty);
                Assert.That(consoles.GetMapState(map.Grid.Owner).Exclusions, Does.Contain(zone));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GreenZoneBlocksShipShotsAndPreservesFireOnVessels()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var transforms = entities.System<SharedTransformSystem>();
            var zones = entities.System<GreenZoneSystem>();
            entities.AddComponent<GreenZoneComponent>(map.Grid);
            var center = transforms.GetMapCoordinates(map.Grid);
            var start = center.Offset(new Vector2(2500f, 0f));
            Assert.That(zones.TryFindBoundary(start, new Vector2(-1000f, 0f), out var hit), Is.True);
            Assert.That(hit, Is.EqualTo(0.5f).Within(0.002f));
            Assert.That(zones.TryFindBoundary(start, new Vector2(1000f, 0f), out _), Is.False);
            Assert.That(zones.TryFindBoundary(start, new Vector2(-100f, 0f), out _), Is.False);

            var actor = entities.SpawnEntity(null, map.GridCoords);
            var gun = entities.SpawnEntity("WeaponPistolMk58", map.GridCoords);
            Assert.That(entities.HasComponent<GunComponent>(gun), Is.True);
            var attempt = new AttemptShootEvent(actor, null);
            entities.EventBus.RaiseLocalEvent(gun, ref attempt);
            Assert.That(attempt.Cancelled, Is.False);
            entities.AddComponent<ShipGunClassComponent>(gun);
            entities.EventBus.RaiseLocalEvent(gun, ref attempt);
            Assert.That(attempt.Cancelled, Is.True);
            var beam = new GetHitscanRangeEvent(start, -Vector2.UnitX, 1000f);
            entities.EventBus.RaiseLocalEvent(gun, ref beam);
            Assert.That(beam.Range, Is.EqualTo(500f).Within(2f));
            Assert.That(zones.IsShipProjectile(gun, new ProjectileComponent { Weapon = gun }), Is.True);
            Assert.That(zones.SuppressesFire(actor), Is.True);

            var maps = entities.System<SharedMapSystem>();
            var vessel = maps.CreateGridEntity(map.MapId);
            transforms.SetCoordinates(vessel.Owner, transforms.ToCoordinates(center.Offset(new Vector2(50f, 0f))));
            entities.AddComponent<VesselComponent>(vessel.Owner);
            var fire = new EntityCoordinates(vessel.Owner, Vector2.Zero);
            Assert.That(zones.SuppressesFire(fire, vessel.Owner), Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
