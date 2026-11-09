using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server._NF.StationEvents.Components;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipGuns;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.GameTicking.Components;
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
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class GreenZoneTests
{
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
