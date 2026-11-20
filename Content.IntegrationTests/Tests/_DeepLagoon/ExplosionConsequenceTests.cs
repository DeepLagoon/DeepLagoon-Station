using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Damage.Components;
using Robust.Server.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExplosionConsequenceTests
{
    [Test]
    public async Task SplitDeferralsNestWithoutChangingSavedOrExternallyUpdatedSettings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var splitting = em.System<GridFixtureSystem>();
            var map = maps.CreateMap(out var id, runMapInit: true);
            var grid = maps.CreateGridEntity(id);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid, Enumerable.Range(0, 8).Select(i => (new Vector2i(i, 0), floor)).ToList());
            var first = splitting.DeferSplitting(grid);
            var second = splitting.DeferSplitting(grid);
            maps.SetTiles(grid, new() { (new(3, 0), Tile.Empty) });
            int Grids()
            {
                var count = 0;
                var query = em.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
                while (query.MoveNext(out _, out _, out var transform))
                    if (transform.MapID == id) count++;
                return count;
            }
            Assert.That(grid.Comp.CanSplit, Is.True);
            Assert.That(Grids(), Is.EqualTo(1));
            second.Dispose();
            second.Dispose();
            Assert.That(Grids(), Is.EqualTo(1), "An inner or repeated dispose must not release the outer scope.");
            grid.Comp.CanSplit = false; // A real gameplay change while the scope is active.
            first.Dispose();
            Assert.That(grid.Comp.CanSplit, Is.False, "A temporary scope must not overwrite a real setting change.");
            Assert.That(Grids(), Is.EqualTo(1));
            grid.Comp.CanSplit = true;
            splitting.CheckSplits(grid);
            Assert.That(Grids(), Is.EqualTo(2));
            em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BatchedFloorDamagePreservesNativeTilesCableDropsSplitsAndChildPositions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        var maps = em.System<SharedMapSystem>();
        var transforms = em.System<SharedTransformSystem>();
        var mapEntities = new List<EntityUid>();
        var ids = new List<MapId>();
        var grids = new List<Entity<MapGridComponent>>();
        var cargo = new List<EntityUid>();
        var changes = new List<(Vector2i, Tile)>();
        ExplosionTileCommit commit = default!;
        await pair.Server.WaitAssertion(() =>
        {
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var i = 0; i < 2; i++)
            {
                mapEntities.Add(maps.CreateMap(out var id, runMapInit: true));
                ids.Add(id);
                var grid = maps.CreateGridEntity(id);
                grids.Add(grid);
                var tiles = new List<(Vector2i, Tile)>();
                for (var x = 0; x < 32; x++)
                for (var y = 0; y < 8; y++)
                    tiles.Add((new(x, y), floor));
                maps.SetTiles(grid, tiles);
                cargo.Add(em.SpawnEntity("SheetSteel1", new EntityCoordinates(grid, new Vector2(28.5f, 4.5f))));
                for (var x = 8; x < 24; x++)
                for (var y = 0; y < 8; y++)
                {
                    if (i == 0)
                        changes.Add((new(x, y), Tile.Empty));
                    foreach (var prototype in new[] { "CableHV", "CableApcExtension" })
                    {
                        var wire = em.SpawnEntity(prototype, new EntityCoordinates(grid, new Vector2(x + .5f, y + .5f)));
                        em.EnsureComponent<GodmodeComponent>(wire);
                        transforms.AnchorEntity(wire);
                    }
                }
            }
            maps.SetTiles(grids[0], changes);
            commit = new(em, maps, em.System<ExplosionSystem>());
            commit.GetChanges(grids[1]).AddRange(changes);
            var checks = 0;
            commit.Flush(() => checks++ < 1, effectsFinished: true);
            Assert.That(commit.Finished, Is.False, "One batch must not consume the entire result.");
            Assert.That(grids[1].Comp.CanSplit, Is.True, "The saved grid setting must not be changed by a temporary deferral.");
        });

        var batches = 1;
        while (!commit.Finished && batches++ < 100)
        {
            await pair.Server.WaitRunTicks(1);
            await pair.Server.WaitAssertion(() =>
            {
                var checks = 0;
                commit.Flush(() => checks++ < 1, effectsFinished: true);
            });
        }
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(commit.Finished, Is.True);
            Assert.That(grids[1].Comp.CanSplit, Is.True);
            var gridCounts = new int[2];
            var tiles = new[] { new HashSet<Vector2>(), new HashSet<Vector2>() };
            var drops = new[] { new Dictionary<string, int>(), new Dictionary<string, int>() };
            var gridQuery = em.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var uid, out var grid, out var transform))
            {
                var index = ids.IndexOf(transform.MapID);
                if (index < 0)
                    continue;
                gridCounts[index]++;
                var offset = transforms.GetWorldPosition(uid);
                foreach (var tile in maps.GetAllTiles(uid, grid))
                    tiles[index].Add((Vector2) tile.GridIndices + offset);
            }
            var query = em.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out _, out var metadata, out var transform))
            {
                var index = ids.IndexOf(transform.MapID);
                var prototype = metadata.EntityPrototype?.ID;
                if (index < 0 || prototype is not ("CableHVStack1" or "CableApcStack1"))
                    continue;
                drops[index][prototype] = drops[index].GetValueOrDefault(prototype) + 1;
            }
            Assert.That(gridCounts, Is.EqualTo(new[] { 2, 2 }));
            Assert.That(tiles[1], Is.EquivalentTo(tiles[0]));
            Assert.That(tiles[1].Count, Is.EqualTo(128));
            Assert.That(drops[1], Is.EquivalentTo(drops[0]));
            Assert.That(drops[1].Values.Sum(), Is.EqualTo(256));
            foreach (var uid in cargo)
            {
                Assert.That(em.EntityExists(uid), Is.True);
                Assert.That(transforms.GetWorldPosition(uid), Is.EqualTo(new Vector2(28.5f, 4.5f)));
                Assert.That(em.GetComponent<TransformComponent>(uid).GridUid, Is.Not.Null);
            }
            commit.Dispose();
            foreach (var map in mapEntities)
                em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CancellingFloorCommitRestoresTheOriginalSplitSetting(bool canSplit)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var map = maps.CreateMap(out var id, runMapInit: true);
            var grid = maps.CreateGridEntity(id);
            grid.Comp.CanSplit = canSplit;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = Enumerable.Range(0, 64).Select(i => (new Vector2i(i, 0), floor)).ToList();
            maps.SetTiles(grid, tiles);
            using (var commit = new ExplosionTileCommit(em, maps, em.System<ExplosionSystem>()))
            {
                commit.GetChanges(grid).AddRange(tiles.Select(t => (t.Item1, Tile.Empty)));
                var checks = 0;
                commit.Flush(() => checks++ < 1, effectsFinished: true);
                Assert.That(commit.Finished, Is.False);
                Assert.That(grid.Comp.CanSplit, Is.EqualTo(canSplit));
            }
            Assert.That(grid.Comp.CanSplit, Is.EqualTo(canSplit));
            em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplosionFinishesOnlyAfterEveryFloorBatchAndCableDrop()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var em = pair.Server.EntMan;
        var maps = em.System<SharedMapSystem>();
        var explosion = em.System<ExplosionSystem>();
        EntityUid map = default;
        MapId id = default;
        Entity<MapGridComponent> grid = default;
        await pair.Server.WaitAssertion(() =>
        {
            map = maps.CreateMap(out id, runMapInit: true);
            grid = maps.CreateGridEntity(id);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 16; x++)
            for (var y = 0; y < 16; y++)
                tiles.Add((new(x, y), floor));
            maps.SetTiles(grid, tiles);
            foreach (var (tile, _) in tiles)
            {
                var wire = em.SpawnEntity("CableHV", new EntityCoordinates(grid, (Vector2) tile + new Vector2(.5f)));
                em.EnsureComponent<GodmodeComponent>(wire);
            }
            explosion.QueueExplosion(new EntityCoordinates(grid, new Vector2(8.5f)), "Radioactive", 90000000, 2000, 4000, null);
        });

        // A large blast is intentionally allowed to span ticks; do not interpret the end
        // of its entity pass as completion while native floor callbacks are still pending.
        var done = false;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        for (var i = 0; i < 30 && !done; i++)
        {
            await pair.Server.WaitRunTicks(30);
            await pair.Server.WaitAssertion(() =>
            {
                done = typeof(ExplosionSystem).GetField("_activeExplosion", flags)!.GetValue(explosion) == null &&
                       typeof(ExplosionSystem).GetField("_tileGeneration", flags)!.GetValue(explosion) == null &&
                       ((System.Collections.ICollection) typeof(ExplosionSystem).GetField("_explosionQueue", flags)!.GetValue(explosion)!).Count == 0;
            });
        }
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(done, Is.True);
            Assert.That(grid.Comp.CanSplit, Is.True);
            var drops = 0;
            var query = em.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out _, out var metadata, out var transform))
                if (transform.MapID == id && metadata.EntityPrototype?.ID == "CableHVStack1")
                    drops++;
            Assert.That(drops, Is.EqualTo(256));
            em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MapRemovalAndNewProtectionSafelyReleasePendingFloorCommits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            foreach (var deleteMap in new[] { false, true })
            {
                var map = maps.CreateMap(out var id, runMapInit: true);
                var grid = maps.CreateGridEntity(id);
                maps.SetTiles(grid, Enumerable.Range(0, 64).Select(i => (new Vector2i(i, 0), floor)).ToList());
                using var commit = new ExplosionTileCommit(em, maps, em.System<ExplosionSystem>());
                commit.GetChanges(grid).AddRange(Enumerable.Range(0, 64).Select(i => (new Vector2i(i, 0), Tile.Empty)));
                var checks = 0;
                commit.Flush(() => checks++ < 1, effectsFinished: true);
                if (deleteMap)
                    em.DeleteEntity(map);
                else
                    em.EnsureComponent<Content.Shared.Tiles.ProtectedGridComponent>(grid).PreventExplosions = true;
                commit.Flush(() => true, effectsFinished: true);
                Assert.That(commit.Finished, Is.True);
                Assert.That(grid.Comp.CanSplit, Is.True);
                if (!deleteMap)
                {
                    Assert.That(maps.GetAllTiles(grid, grid.Comp).Count(), Is.EqualTo(48), "New protection must stop subsequent batches.");
                    em.DeleteEntity(map);
                }
            }
        });
        await pair.CleanReturnAsync();
    }
}
