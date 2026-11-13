using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server.Hands.Systems;
using Content.Server.Power.Components;
using Content.Server.Research.Disk;
using Content.Server.Research.Disk;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Access.Components;
using Content.Shared.Research.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using Robust.Client.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class NavigationEncounterTests
{
    [Test]
    public async Task NavigationRelayMaterializesOffscreenAndReachesTheConnectedClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        EntityUid map = default, actor = default;
        NetEntity relayNet = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition { Id = "relay-view", Seed = 713,
                Regions = new() { new SpaceRegion { Id = "dust", Kind = SpaceRegionKind.Dust, X = 5000, Radius = 500 } } };
            actor = entities.SpawnEntity("MobObserver", new EntityCoordinates(map, new Vector2(5000, 0)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, actor);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var signals = entities.EntityQueryEnumerator<NavigationSignalComponent, TransformComponent>();
            EntityUid relay = default;
            while (signals.MoveNext(out var uid, out _, out var transform)) if (transform.MapUid == map) relay = uid;
            Assert.That(relay, Is.Not.EqualTo(EntityUid.Invalid));
            var position = entities.System<SharedTransformSystem>().GetWorldPosition(relay);
            Assert.That(Vector2.Distance(position, new Vector2(5000, 0)), Is.GreaterThan(100), "The relay must begin beyond the observer horizon");
            Assert.That(entities.System<OrbitalDebrisSystem>().ActiveCount, Is.LessThanOrEqualTo(5));
            relayNet = entities.GetNetEntity(relay);
            entities.System<SharedTransformSystem>().SetWorldPosition(actor, position + new Vector2(10, 0));
        });
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var relay = entities.GetEntity(relayNet);
            Assert.That(entities.EntityExists(relay), Is.True);
            Assert.That(entities.GetComponent<SpriteComponent>(relay).Visible, Is.True);
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlasmaAsteroidsUseChromiteFloorAndMineableRocksAndMove()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid map = default, asteroid = default;
        Vector2 start = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition { Regions = new()
                { new SpaceRegion { Id = "plasma", Kind = SpaceRegionKind.Plasma, X = 5000, Radius = 2000 } } };
            asteroid = entities.System<OrbitalDebrisSystem>().SpawnPile(map, new Vector2(5000, 0), new Vector2(5000, 1000), 713)!.Value;
            Assert.That(entities.HasComponent<SpaceChromiteAsteroidComponent>(asteroid), Is.True);
            var grid = entities.GetComponent<MapGridComponent>(asteroid);
            Assert.That(grid.LocalAABB.Size.X, Is.InRange(1, 10));
            Assert.That(grid.LocalAABB.Size.Y, Is.InRange(1, 10));
            var tiles = entities.System<SharedMapSystem>().GetAllTiles(asteroid, grid);
            var count = 0;
            while (tiles.MoveNext())
            {
                count++;
                Assert.That(tiles.Current.Tile.TypeId, Is.EqualTo(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorChromite"].TileId));
            }
            Assert.That(count, Is.LessThanOrEqualTo(36));
            var rocks = 0;
            var children = entities.GetComponent<TransformComponent>(asteroid).ChildEnumerator;
            while (children.MoveNext(out var child))
                if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID.StartsWith("WallRockChromite", StringComparison.Ordinal) == true) rocks++;
            Assert.That(rocks, Is.GreaterThan(0));
            start = entities.System<SharedTransformSystem>().GetWorldPosition(asteroid);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(Vector2.Distance(start, entities.System<SharedTransformSystem>().GetWorldPosition(asteroid)), Is.GreaterThan(0.5f));
            Assert.That(entities.System<OrbitalDebrisSystem>().ActiveCount, Is.EqualTo(1));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EverySignalKindRecoversNativeRewardsOnceAndChecksConsoleAuthority()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out var mapId);
            var definition = new SectorDefinition { Id = "native-navigation", Seed = 713, Planets = new()
                { new SectorPlanet { Id = "planet", X = 50000 } }, Regions = new()
                {
                    new SpaceRegion { Id = "dust", Kind = SpaceRegionKind.Dust, X = 10000, Radius = 500 },
                    new SpaceRegion { Id = "archive", Kind = SpaceRegionKind.Debris, X = 20000, Radius = 500 },
                    new SpaceRegion { Id = "radiation", Kind = SpaceRegionKind.Radiation, X = 30000, Radius = 500 },
                    new SpaceRegion { Id = "capacitor", Kind = SpaceRegionKind.Plasma, X = 40000, Radius = 500 },
                } };
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime.TotalSeconds;
            while (!PlasmaWeather.Quiet(NavigationSiteCatalog.Generate(definition).Single(s => s.Kind == NavigationSiteKind.Capacitor).Seed, now)) definition.Seed++;
            var environment = entities.EnsureComponent<SpaceEnvironmentComponent>(map);
            environment.Campaign = "navigation-integration";
            environment.Definition = definition;
            var store = entities.System<SectorSystem>().Store;
            store.GetOrCreate(environment.Campaign, definition);
            var navigation = entities.System<SpaceNavigationSystem>();
            var sites = navigation.Sites(map, environment).Sites;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            foreach (var kind in Enum.GetValues<NavigationSiteKind>())
            {
                var site = sites.First(s => s.Kind == kind);
                var grid = maps.CreateGridEntity(mapId);
                for (var x = 0; x < 3; x++) maps.SetTile(grid, new Vector2i(x, 0), floor);
                entities.System<SharedTransformSystem>().SetWorldPosition(grid, site.Position - new Vector2(0.5f));
                var console = entities.SpawnEntity("ComputerShuttle", new EntityCoordinates(grid, new Vector2(0.5f)));
                var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid, new Vector2(0.5f)));
                entities.RemoveComponent<AccessReaderComponent>(console);
                entities.RemoveComponent<ShuttleConsoleLockComponent>(console);
                var power = entities.GetComponent<ApcPowerReceiverComponent>(console);
                power.Powered = false;
                Assert.That(navigation.Recover(console, actor, true), Is.False, kind.ToString());
                power.Powered = true;
                entities.System<SharedPhysicsSystem>().SetLinearVelocity(grid, new Vector2(10, 0));
                Assert.That(navigation.Recover(console, actor, true), Is.False, "Fast recovery must fail");
                entities.System<SharedPhysicsSystem>().SetLinearVelocity(grid, Vector2.Zero);
                Assert.That(navigation.Scan(console, actor), Is.True);
                if (kind == NavigationSiteKind.Satellite)
                {
                    Assert.That(navigation.Recover(console, actor, false), Is.False, "Repair needs cable");
                    var cable = entities.SpawnEntity("CableApcStack10", new EntityCoordinates(grid, new Vector2(0.5f)));
                    Assert.That(entities.System<HandsSystem>().TryPickupAnyHand(actor, cable), Is.True);
                    Assert.That(navigation.Recover(console, actor, false), Is.True, kind.ToString());
                    Assert.That(entities.GetComponent<StackComponent>(cable).Count, Is.EqualTo(5));
                }
                else Assert.That(navigation.Recover(console, actor, kind != NavigationSiteKind.Distress), Is.True, kind.ToString());
                Assert.That(store.Encounters(environment.Campaign, definition.Id), Does.Contain(site.Id));
                Assert.That(navigation.Recover(console, actor, false), Is.False, "A second outcome must never be granted");
            }
            Assert.That(store.Encounters(environment.Campaign, definition.Id), Has.Count.EqualTo(6));
            var disks = entities.EntityQueryEnumerator<ResearchDiskComponent, TransformComponent>();
            var rewards = 0;
            while (disks.MoveNext(out _, out var disk, out var transform))
                if (transform.MapUid == map) { rewards++; Assert.That(disk.Points, Is.GreaterThanOrEqualTo(1500)); }
            Assert.That(rewards, Is.EqualTo(2));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RadiationPocketActuallyAttenuatesTheNativeReceiver()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out _);
            var region = new SpaceRegion { Id = "rad", Kind = SpaceRegionKind.Radiation, Radius = 500, Intensity = 1 };
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition { Regions = new() { region } };
            var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, NavigationSiteCatalog.RegionPoint(region, new Vector2(0.4f, 0))));
            entities.EnsureComponent<RadiationReceiverComponent>(actor);
            var pocket = new GetAmbientRadiationEvent();
            entities.EventBus.RaiseLocalEvent(actor, ref pocket);
            Assert.That(pocket.Radiation, Is.EqualTo(0.1f).Within(0.001));
            entities.System<SharedTransformSystem>().SetWorldPosition(actor, Vector2.Zero);
            var exposed = new GetAmbientRadiationEvent();
            entities.EventBus.RaiseLocalEvent(actor, ref exposed);
            Assert.That(exposed.Radiation, Is.EqualTo(0.5f).Within(0.001));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
