using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Item;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Station.Components;
using Content.Server._DeepLagoon.Mapping;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class OrbitalDebrisIntegrationTests
{
    [Test]
    public async Task PilesMoveStaySmallRespectLimitsAndPreservePlayerChanges()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid map = default;
        EntityUid moving = default;
        EntityUid cargo = default;
        EntityUid rebuilt = default;
        Vector2 start = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out _);
            var debris = entities.System<OrbitalDebrisSystem>();
            for (uint i = 0; i < OrbitalDebrisSystem.MapLimit; i++)
            {
                var uid = debris.SpawnPile(map, new Vector2(100 + i * 100, 50), Vector2.Zero, i + 1)!.Value;
                var grid = entities.GetComponent<MapGridComponent>(uid);
                Assert.That(grid.LocalAABB.Size.X, Is.InRange(1, 4));
                Assert.That(grid.LocalAABB.Size.Y, Is.InRange(1, 4));
                var physics = entities.GetComponent<PhysicsComponent>(uid);
                Assert.That(physics.BodyType, Is.EqualTo(BodyType.Dynamic));
                var tiles = maps.GetAllTiles(uid, grid);
                while (tiles.MoveNext())
                    Assert.That(tiles.Current.Tile.TypeId, Is.EqualTo(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
                var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
                var walls = 0;
                while (children.MoveNext(out var child))
                {
                    var proto = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                    if (proto?.StartsWith("WallRockScrapPile", StringComparison.Ordinal) != true) continue;
                    walls++;
                    Assert.That(entities.GetComponent<TransformComponent>(child).Anchored, Is.True);
                }
                Assert.That(walls, Is.GreaterThan(0));
                Assert.That(entities.GetComponent<MetaDataComponent>(uid).EntityName, Is.EqualTo("space junk").Or.EqualTo("космический мусор"));
                Assert.That(physics.LinearVelocity.Length(), Is.InRange(1.99f, 6.01f));
                if (i == 0) { moving = uid; start = entities.System<SharedTransformSystem>().GetWorldPosition(uid); }
                if (i == 1) cargo = uid;
                if (i == 2) rebuilt = uid;
            }
            Assert.That(debris.SpawnPile(map, new Vector2(9999, 100), Vector2.Zero, 99), Is.Null);
            entities.SpawnEntity(null, new EntityCoordinates(cargo, new Vector2(0.5f, 0.5f)));
            var rebuiltGrid = entities.GetComponent<MapGridComponent>(rebuilt);
            var firstTile = maps.GetTileRef(rebuilt, rebuiltGrid, Vector2i.Zero).Tile;
            maps.SetTile(rebuilt, rebuiltGrid, new Vector2i(4, 0), firstTile);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var debris = entities.System<OrbitalDebrisSystem>();
            Assert.That(Vector2.Distance(start, entities.System<SharedTransformSystem>().GetWorldPosition(moving)), Is.GreaterThan(0.5f));
            debris.ClearUnclaimed(map);
            Assert.That(entities.EntityExists(moving), Is.False);
            Assert.That(entities.EntityExists(cargo), Is.True, "Cargo-bearing grids must survive automatic cleanup");
            Assert.That(entities.EntityExists(rebuilt), Is.True, "Player construction must survive automatic cleanup");
            Assert.That(debris.ActiveCount, Is.Zero);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProtectedPoiOrbitsVisibilityCapsAndDetachedLootAreEnforced()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid junkMap = default, poiMap = default;
        EntityUid loose = default, unrelated = default, stored = default;
        EntityUid[] merged = null;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var debris = entities.System<OrbitalDebrisSystem>();
            var transform = entities.System<SharedTransformSystem>();
            poiMap = maps.CreateMap(out var poiMapId);
            var loader = entities.System<MapLoaderSystem>();
            Assert.That(loader.TryLoadGridTracked(poiMapId, new ResPath("/Maps/_Mono/POI/zetanode.yml"), out var zeta), Is.True);
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            entities.System<Content.Server.Station.Systems.StationSystem>().AddGridToStation(station, zeta.Value.Owner);
            var center = transform.GetWorldPosition(zeta.Value.Owner) + zeta.Value.Comp.LocalAABB.Center;
            Assert.That(debris.SafeOrbit(poiMap, center + new Vector2(500, 0), center + new Vector2(600, 0)), Is.False);
            Assert.That(debris.SafeOrbit(poiMap, center + new Vector2(4000, 0), center + new Vector2(2000, 0)), Is.False,
                "A distant spawn is unsafe when its orbit later crosses the Zeta Node exclusion zone");
            Assert.That(debris.SpawnPile(poiMap, center + new Vector2(4000, 0), center + new Vector2(2000, 0), 41), Is.Null);
            entities.EnsureComponent<SpaceJunkExclusionComponent>(zeta.Value.Owner);
            entities.EnsureComponent<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(zeta.Value.Owner);
            Assert.That(debris.SafeOrbit(poiMap, center + new Vector2(500, 0), center + new Vector2(600, 0)), Is.False,
                "Explicit POIs stay protected even if ownership information is present");
            Assert.That(debris.SafeOrbit(poiMap, center + new Vector2(4000, 0), center + new Vector2(4500, 0)), Is.True);
            entities.DeleteEntity(station);
            entities.DeleteEntity(poiMap);

            junkMap = maps.CreateMap(out var junkMapId);
            var ids = new List<EntityUid>();
            for (uint i = 0; i < 20; i++)
                ids.Add(debris.SpawnPile(junkMap, new Vector2(1000 + i * 10, 1000), new Vector2(5000, 0), i + 713)!.Value);
            merged = ids.ToArray();
            var first = new OrbitalDebrisSystem.Observer(junkMap, new Vector2(1090, 1000), 200);
            var second = new OrbitalDebrisSystem.Observer(junkMap, new Vector2(1100, 1000), 200);
            debris.EnforceViewLimit(new[] { first });
            debris.EnforceViewLimit(new[] { second });

            EntityUid FindLoot(EntityUid grid)
            {
                var children = entities.GetComponent<TransformComponent>(grid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    if (!entities.GetComponent<TransformComponent>(child).Anchored && entities.HasComponent<ItemComponent>(child)) return child;
                return EntityUid.Invalid;
            }
            for (uint seed = 1; seed < 30 && (loose == EntityUid.Invalid || stored == EntityUid.Invalid); seed++)
            {
                var uid = debris.SpawnPile(junkMap, new Vector2(2000 + seed * 20, 1000), new Vector2(5000, 0), seed)!.Value;
                var loot = FindLoot(uid);
                if (loot == EntityUid.Invalid) continue;
                if (loose == EntityUid.Invalid) loose = loot;
                else stored = loot;
            }
            Assert.That(loose, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(stored, Is.Not.EqualTo(EntityUid.Invalid));
            transform.SetCoordinates(loose, new EntityCoordinates(junkMap, new Vector2(8000, 0)));
            var playerGrid = maps.CreateGridEntity(junkMapId);
            maps.SetTile(playerGrid, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            transform.SetWorldPosition(playerGrid, new Vector2(9000, 0));
            var physics = entities.System<Robust.Shared.Physics.Systems.SharedPhysicsSystem>();
            physics.SetBodyType(playerGrid, BodyType.Dynamic);
            physics.SetLinearVelocity(playerGrid, new Vector2(100, 0));
            var eye = entities.SpawnEntity(null, new EntityCoordinates(playerGrid, new Vector2(0.5f)));
            var velocity = physics.GetMapLinearVelocity(eye);
            Assert.That(velocity.X, Is.EqualTo(100f).Within(0.01f), "Prediction must include parent ship velocity");
            var observer = new OrbitalDebrisSystem.Observer(junkMap, transform.GetWorldPosition(eye), 80, velocity);
            var ahead = OrbitalDebrisSystem.ForecastSpawn(observer, 0, 0, 0);
            Assert.That(ahead.X - observer.Position.X, Is.GreaterThanOrEqualTo(80 + 108 * 8 + 16 - 0.01f));
            transform.SetCoordinates(stored, new EntityCoordinates(playerGrid, new Vector2(0.5f)));
            unrelated = entities.SpawnEntity("Crowbar", new EntityCoordinates(junkMap, new Vector2(10000, 0)));
        });
        await pair.RunTicksSync(4);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(merged.Count(entities.EntityExists), Is.LessThanOrEqualTo(10), "Converging views must share ten grids");
            Assert.That(entities.EntityExists(loose), Is.False, "Invisible detached generated loot must be removed without a grace timer");
            Assert.That(entities.EntityExists(stored), Is.True, "Finds taken onto a player's ordinary grid must survive");
            Assert.That(entities.EntityExists(unrelated), Is.True, "Unrelated items are outside generated-loot ownership");
            entities.DeleteEntity(junkMap);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(SpaceRegionKind.Debris, false)]
    [TestCase(SpaceRegionKind.Asteroids, false)]
    [TestCase(SpaceRegionKind.Asteroids, true)]
    public async Task DebrisFieldsUseLargeSparseOrePilesAndRicherLoot(SpaceRegionKind kind, bool overlapping)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out _);
            var environment = entities.EnsureComponent<SpaceEnvironmentComponent>(map);
            environment.Definition = new SectorDefinition { Id = "field-test", Regions = new()
                { new SpaceRegion { X = 5000, Radius = 1500, Kind = kind } } };
            if (overlapping) environment.Definition.Regions.Insert(0,
                new SpaceRegion { X = 5000, Radius = 1500, Kind = SpaceRegionKind.Debris });
            var debris = entities.System<OrbitalDebrisSystem>();
            var large = false;
            var ores = new HashSet<string>();
            var fauna = 0;
            var fragments = 0;
            for (uint seed = 1; seed <= 24; seed++)
            {
                var uid = debris.SpawnPile(map, new Vector2(4200 + seed * 50, 0), new Vector2(5000, 1000), seed)!.Value;
                var grid = entities.GetComponent<MapGridComponent>(uid);
                Assert.That(grid.LocalAABB.Size.X, Is.InRange(1, 10));
                Assert.That(grid.LocalAABB.Size.Y, Is.InRange(1, 10));
                large |= grid.LocalAABB.Size.X > 4 || grid.LocalAABB.Size.Y > 4;
                var tiles = maps.GetAllTiles(uid, grid);
                var count = 0;
                while (tiles.MoveNext()) count++;
                Assert.That(count, Is.LessThanOrEqualTo(36));
                var children = entities.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    var proto = entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                    if (proto?.StartsWith("WallRockScrapPile", StringComparison.Ordinal) == true) ores.Add(proto);
                    if (proto == "MobCarpSalvage") fauna++;
                    if (proto == "ArtifactFragment1") fragments++;
                }
            }
            Assert.That(large, Is.True);
            Assert.That(ores.Count, Is.GreaterThan(3));
            Assert.That(fauna, Is.InRange(1, 24));
            if (kind == SpaceRegionKind.Asteroids)
                Assert.That(fragments, Is.GreaterThan(0), "Asteroid-stream piles must materialize artifact fragments, including in overlapping fields");
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnseenPilesAreRemovedAutomatically()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid map = default;
        EntityUid pile = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out _);
            pile = entities.System<OrbitalDebrisSystem>().SpawnPile(map, new Vector2(100, 0), Vector2.Zero, 34)!.Value;
        });
        await pair.RunTicksSync(720);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.EntityExists(pile), Is.False);
            Assert.That(pair.Server.EntMan.System<OrbitalDebrisSystem>().ActiveCount, Is.Zero);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
