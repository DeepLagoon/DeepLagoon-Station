using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Content.Server._Mono.GridClaimer;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server.Shuttles.Components;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SpaceStreamEncounterTests
{
    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(2u)]
    [TestCase(3u)]
    [TestCase(4u)]
    public async Task ExistingDerelictMapsMoveAndUseDurableSlots(uint seed)
    {
        int[] serverSeeds = [1, 790453266, 2, 440242169, 166708317];
        int[] clientSeeds = [1, 1156095396, 2, 1538702929, 1496590806];
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false,
            ServerSeed = serverSeeds[seed], ClientSeed = clientSeeds[seed] });
        EntityUid map = default, wreck = default;
        Vector2 start = default;
        var originalChildren = new HashSet<EntityUid>();
        var originalTiles = new Dictionary<Vector2i, Tile>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            var definition = SectorGenerator.Generate($"derelict-assets-{seed}", 713, new SectorProfilePrototype());
            definition.Regions = new() { new SpaceRegion { Id = "stream", X = 5000, Radius = 2000, Kind = SpaceRegionKind.Asteroids } };
            var environment = entities.EnsureComponent<SpaceEnvironmentComponent>(map);
            environment.Campaign = "encounter-tests";
            environment.Definition = definition;
            var store = entities.System<SectorSystem>().Store;
            store.GetOrCreate(environment.Campaign, definition);
            var spawned = entities.System<OrbitalDebrisSystem>().SpawnPile(map, new Vector2(5000, 0), new Vector2(5000, 1000), seed);
            if (spawned == null)
            {
                var rejected = new List<string>();
                var query = entities.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out var grid, out var transform))
                {
                    if (transform.MapUid != map) continue;
                    var rejectedTiles = entities.System<SharedMapSystem>().GetAllTiles(uid, grid);
                    var tileCount = 0;
                    while (rejectedTiles.MoveNext()) tileCount++;
                    var pending = new Stack<EntityUid>();
                    pending.Push(uid);
                    var count = 0;
                    while (pending.TryPop(out var child) && count++ < 2048)
                    {
                        var rejectedChildren = entities.GetComponent<TransformComponent>(child).ChildEnumerator;
                        while (rejectedChildren.MoveNext(out var nested)) pending.Push(nested);
                    }
                    rejected.Add($"tiles={tileCount}, bounds={grid.LocalAABB}, radius={grid.LocalAABB.Size.Length() * 0.5f}, descendants={count-1}, shuttle={entities.HasComponent<ShuttleComponent>(uid)}");
                }
                Assert.Fail(OrbitalDebrisSystem.DerelictMaps[seed] + ": " + string.Join("; ", rejected));
            }
            wreck = spawned!.Value;
            Assert.That(entities.GetComponent<GridMapSourceComponent>(wreck).Source, Is.EqualTo(OrbitalDebrisSystem.DerelictMaps[seed]));
            Assert.That(entities.HasComponent<ShuttleComponent>(wreck), Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(wreck).EntityName, Does.StartWith("Derelict-"));
            Assert.That(entities.GetComponent<SpaceDerelictComponent>(wreck).Slot, Is.Zero);
            Assert.That(store.DerelictSlots(environment.Campaign, definition.Id, "stream"), Is.EqualTo(1));
            Assert.That(entities.GetComponent<MapGridComponent>(wreck).LocalAABB.Size.Length() * 0.5f, Is.LessThanOrEqualTo(32));
            start = entities.System<SharedTransformSystem>().GetWorldPosition(wreck);
            var children = entities.GetComponent<TransformComponent>(wreck).ChildEnumerator;
            while (children.MoveNext(out var child)) originalChildren.Add(child);
            var tiles = entities.System<SharedMapSystem>().GetAllTiles(wreck, entities.GetComponent<MapGridComponent>(wreck));
            while (tiles.MoveNext()) originalTiles.Add(tiles.Current.GridIndices, tiles.Current.Tile);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            if (entities.System<OrbitalDebrisSystem>().ActiveCount != 1)
            {
                var details = new List<string>();
                if (entities.EntityExists(wreck))
                {
                    var transform = entities.GetComponent<TransformComponent>(wreck);
                    details.Add($"map={transform.MapUid}, expected={map}");
                    details.Add($"claimed={entities.GetComponent<ClaimableGridComponent>(wreck).Claimed}");
                    var children = transform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                        if (!originalChildren.Contains(child)) details.Add($"new child: {entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID} ({child})");
                    var tiles = entities.System<SharedMapSystem>().GetAllTiles(wreck, entities.GetComponent<MapGridComponent>(wreck));
                    while (tiles.MoveNext())
                        if (!originalTiles.TryGetValue(tiles.Current.GridIndices, out var old) || old.TypeId != tiles.Current.Tile.TypeId)
                            details.Add($"tile {tiles.Current.GridIndices}: {old.TypeId}->{tiles.Current.Tile.TypeId}");
                }
                else details.Add("deleted");
                Assert.Fail("Loaded map contents must not accidentally release the derelict: " + string.Join("; ", details.Take(30)));
            }
            Assert.That(Vector2.Distance(start, entities.System<SharedTransformSystem>().GetWorldPosition(wreck)), Is.GreaterThan(0.5f));
        });
        await pair.RunTicksSync(660);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.EntityExists(wreck), Is.False, "An untouched offscreen derelict must not remain a permanent simulation cost");
            var environment = entities.GetComponent<SpaceEnvironmentComponent>(map);
            Assert.That(entities.System<SectorSystem>().Store.DerelictSlots(environment.Campaign, environment.Definition.Id, "stream"), Is.EqualTo(1));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AsteroidStreamsContainMovingBlueRadarWrecks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid map = default, blue = default;
        Vector2 start = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition { Regions = new()
                { new SpaceRegion { X = 5000, Radius = 2000, Kind = SpaceRegionKind.Asteroids } } };
            var debris = entities.System<OrbitalDebrisSystem>();
            for (uint seed = 1; seed <= 24; seed++)
            {
                var uid = debris.SpawnPile(map, new Vector2(4200 + seed * 50, 0), new Vector2(5000, 1000), seed)!.Value;
                if (!entities.HasComponent<SpaceBlueWreckComponent>(uid)) continue;
                blue = uid;
                var iff = entities.GetComponent<IFFComponent>(uid);
                Assert.That(iff.Color, Is.EqualTo(Color.FromHex("#88b0d1")));
                Assert.That((iff.Flags & IFFFlags.AlwaysShowColor) != 0, Is.True);
                Assert.That(entities.GetComponent<PhysicsComponent>(uid).LinearVelocity.Length(), Is.InRange(1.99f, 6.01f));
            }
            Assert.That(blue, Is.Not.EqualTo(EntityUid.Invalid));
            start = entities.System<SharedTransformSystem>().GetWorldPosition(blue);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(Vector2.Distance(start, entities.System<SharedTransformSystem>().GetWorldPosition(blue)), Is.GreaterThan(0.5f));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
