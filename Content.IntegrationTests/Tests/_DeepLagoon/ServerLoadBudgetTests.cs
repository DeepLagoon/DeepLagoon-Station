#nullable enable annotations
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Performance;
using Content.Server.Explosion.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using FloodResult = (int Area, System.Collections.Generic.List<float> Intensities, Content.Server.Explosion.EntitySystems.ExplosionSpaceTileFlood? Space, System.Collections.Generic.Dictionary<Robust.Shared.GameObjects.EntityUid, Content.Server.Explosion.EntitySystems.ExplosionGridTileFlood> Grids, System.Numerics.Matrix3x2 Matrix);

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ServerLoadBudgetTests
{
    [Test]
    public async Task MapRetirementIsIncrementalAndDeletesItsCompleteSubtreesOnly()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        EntityUid map = default, outside = default;
        var children = new List<EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan; var maps = em.System<SharedMapSystem>();
            map = maps.CreateMap(out var id, runMapInit: true);
            var grid = maps.CreateGridEntity(id);
            outside = em.SpawnEntity(null, MapCoordinates.Nullspace);
            for (var i = 0; i < 1024; i++)
            {
                var root = em.SpawnEntity(null, new EntityCoordinates(grid.Owner, new Vector2(i % 32, i / 32)));
                children.Add(root);
                children.Add(em.SpawnEntity(null, new EntityCoordinates(root, Vector2.Zero)));
            }
            var deletion = em.System<StagedMapDeletionSystem>();
            deletion.Queue(map); deletion.Queue(map);
            Assert.That(em.EntityExists(map), Is.True);
            Assert.That(maps.IsPaused(id), Is.True);
            deletion.Update(0);
            Assert.That(em.EntityExists(map), Is.True, "A single pass must not recursively consume the whole map.");
        });
        await pair.Server.WaitRunTicks(100);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.EntityExists(map), Is.False);
            Assert.That(children.Any(em.EntityExists), Is.False, "Child entities must not escape into nullspace.");
            Assert.That(em.EntityExists(outside), Is.True);
            em.DeleteEntity(outside);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(30)]
    [TestCase(90)]
    public async Task LazyReferenceEdgesMatchTheCompleteTransformedBlockerMap(int degrees)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan; var maps = em.System<SharedMapSystem>();
            var map = maps.CreateMap(out var id, runMapInit: true);
            var reference = maps.CreateGridEntity(id); var other = maps.CreateGridEntity(id);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -4; x <= 4; x++) for (var y = -4; y <= 4; y++) tiles.Add((new(x, y), tile));
            maps.SetTiles(reference, tiles); maps.SetTiles(other, tiles);
            // A distant island on the reference grid must still be represented exactly, without an eager copy.
            maps.SetTile(reference, new Vector2i(1000, 0), tile);
            var tf = em.System<SharedTransformSystem>();
            tf.SetWorldPosition(other.Owner, new Vector2(12, 0)); tf.SetWorldRotation(other.Owner, Angle.FromDegrees(degrees));
            var system = em.System<ExplosionSystem>();
            var epicenter = new MapCoordinates(Vector2.Zero, id);
            var grids = new List<EntityUid> { reference.Owner, other.Owner };
            var (expected, size) = system.TransformGridEdges(epicenter, reference.Owner, grids, 32);
            system.GetUnblockedDirections(expected, size);
            var lazy = new ExplosionSpaceTileFlood(system, epicenter, reference.Owner, grids, 32);
            var lookup = typeof(ExplosionSpaceTileFlood).GetMethod("TryGetBlocker", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var point in expected.Keys.Concat([new Vector2i(500, 500)]))
            {
                object?[] args = [point, null];
                var found = (bool)lookup.Invoke(lazy, args)!;
                Assert.That(found, Is.EqualTo(expected.TryGetValue(point, out var blocker)), point.ToString());
                if (!found) continue;
                var actual = (BlockedSpaceTile)args[1]!;
                Assert.That(actual.UnblockedDirections, Is.EqualTo(blocker!.UnblockedDirections), point.ToString());
                Assert.That(actual.BlockingGridEdges.Select(e => (e.Grid, e.Tile)),
                    Is.EqualTo(blocker.BlockingGridEdges.Select(e => (e.Grid, e.Tile))), point.ToString());
            }
            em.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ResumingExplosionPreparationPreservesAreaIntensitiesAndTiles()
    {
        TestContext.Progress.WriteLine("Explosion resume regression: starting pair");
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        TestContext.Progress.WriteLine("Explosion resume regression: pair ready");
        EntityUid map = default; object generation = null!; IEnumerator<object?> steps = null!;
        FloodResult expected = default; int yields = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan; var maps = em.System<SharedMapSystem>();
            map = maps.CreateMap(out var id, runMapInit: true);
            var grid = maps.CreateGridEntity(id);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid, Enumerable.Range(-8, 17).SelectMany(x => Enumerable.Range(-8, 17).Select(y => (new Vector2i(x, y), tile))).ToList());
            var system = em.System<ExplosionSystem>();
            object[] parameters = [new MapCoordinates(Vector2.Zero, id), "Default", 3000f, 5f, 100f];
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            TestContext.Progress.WriteLine("Explosion resume regression: computing reference fill");
            expected = (FloodResult)typeof(ExplosionSystem).GetMethod("GetExplosionTiles", flags)!.Invoke(system, parameters)!;
            TestContext.Progress.WriteLine("Explosion resume regression: reference fill complete");
            var flood = expected.Grids[grid.Owner];
            var edges = (Dictionary<Vector2i, ExplosionSystem.NeighborFlag>)typeof(ExplosionGridTileFlood).GetField("_edgeTiles", flags)!.GetValue(flood)!;
            var eagerSpace = new HashSet<Vector2i>();
            foreach (var (edge, directions) in edges)
            for (var i = 0; i < ExplosionSystem.NeighbourVectors.Length; i++)
                if ((directions & (ExplosionSystem.NeighborFlag)(1 << i)) != 0) eagerSpace.Add(edge + ExplosionSystem.NeighbourVectors[i]);
            var isSpace = typeof(ExplosionGridTileFlood).GetMethod("IsSpaceTile", flags)!;
            for (var x = -10; x <= 10; x++) for (var y = -10; y <= 10; y++)
            {
                var point = new Vector2i(x, y);
                Assert.That((bool)isSpace.Invoke(flood, [point])!, Is.EqualTo(eagerSpace.Contains(point)), point.ToString());
            }
            var type = typeof(ExplosionSystem).GetNestedType("TileGeneration", BindingFlags.NonPublic)!;
            generation = Activator.CreateInstance(type, true)!;
            steps = ((IEnumerable<object?>)typeof(ExplosionSystem).GetMethod("GenerateExplosionTiles", flags)!
                .Invoke(system, parameters.Append(generation).ToArray())!).GetEnumerator();
        });
        var finished = false;
        for (var tick = 0; tick < 500 && !finished; tick++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                for (var i = 0; i < 4; i++) { if (!steps.MoveNext()) { finished = true; break; } yields++; }
            });
            if (!finished) await pair.Server.WaitRunTicks(1);
        }
        TestContext.Progress.WriteLine($"Explosion resume regression: resumed fill complete={finished} yields={yields}");
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(finished, Is.True); Assert.That(yields, Is.GreaterThan(4));
            var actual = (FloodResult)generation.GetType().GetField("Result")!.GetValue(generation)!;
            Assert.That(actual.Area, Is.EqualTo(expected.Area));
            Assert.That(actual.Intensities, Is.EqualTo(expected.Intensities));
            static string Tiles(FloodResult result) => string.Join(";", result.Grids.OrderBy(p => p.Key.ToString())
                .SelectMany(p => p.Value.TileLists.OrderBy(q => q.Key).Select(q => p.Key + "/" + q.Key + "/" +
                    string.Join(",", q.Value.OrderBy(t => t.X).ThenBy(t => t.Y))))) + "|" +
                string.Join(";", result.Space?.TileLists.OrderBy(p => p.Key).Select(p => p.Key + "/" + string.Join(",", p.Value.OrderBy(t => t.X).ThenBy(t => t.Y))) ?? []);
            Assert.That(Tiles(actual), Is.EqualTo(Tiles(expected)));
            steps.Dispose(); pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
