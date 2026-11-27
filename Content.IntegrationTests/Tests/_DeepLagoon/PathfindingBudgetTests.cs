using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Content.Server.NPC.Pathfinding;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PathfindingBudgetTests
{
    [Test]
    public async Task CancelledRequestsAreCompletedWithoutStartingSearch()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var pathfinding = entities.System<PathfindingSystem>();
            var map = maps.CreateMap(out _);
            try
            {
                using var cancellation = new CancellationTokenSource();
                var coords = new EntityCoordinates(map, Vector2.Zero);
                var tasks = Enumerable.Range(0, 1000)
                    .Select(_ => pathfinding.GetPath(coords, coords, 0, 0, 0, cancellation.Token)).ToArray();
                Assert.That(tasks.All(task => !task.IsCompleted), Is.True);
                cancellation.Cancel();
                pathfinding.Update(0);
                Assert.That(tasks.Count(task => task.IsCompletedSuccessfully), Is.EqualTo(256),
                    "Cancellation must obey the per-tick request cap");
                for (var i = 0; i < 3; i++)
                    pathfinding.Update(0);
                Assert.That(tasks.All(task => task.IsCompletedSuccessfully), Is.True);
                Assert.That(tasks.All(task => task.GetAwaiter().GetResult().Result == PathResult.NoPath), Is.True,
                    "Cancelled requests must not take the start==end success path");
            }
            finally
            {
                entities.DeleteEntity(map);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryGridBacklogIsBoundedFairAndEventuallyDrained()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var server = pair.Server;
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var pathfinding = entities.System<PathfindingSystem>();
            var map = maps.CreateMap(out var mapId);
            var grids = new List<Entity<GridPathfindingComponent>>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var grid = maps.CreateGridEntity(mapId);
                    var component = entities.EnsureComponent<GridPathfindingComponent>(grid.Owner);
#pragma warning disable RA0002 // Seed the same queue used by collision/tile changes, without unrelated systems ticking.
                    component.DirtyChunks.Clear();
                    for (var chunk = 0; chunk < 33; chunk++)
                        component.DirtyChunks.Add(new Vector2i(chunk, i));
                    component.NextUpdate = TimeSpan.Zero;
#pragma warning restore RA0002
                    grids.Add((grid.Owner, component));
                }

                // A pool pair also has startup/lobby grids. Isolate this benchmark's
                // work in its disposable world so those unrelated queues do not
                // consume the four-grids fairness observation window.
                var workload = grids.Select(grid => grid.Owner).ToHashSet();
                var background = entities.EntityQueryEnumerator<GridPathfindingComponent>();
                while (background.MoveNext(out var uid, out var component))
                    if (!workload.Contains(uid))
#pragma warning disable RA0002
                        component.DirtyChunks.Clear();
#pragma warning restore RA0002

                var batches = 0;
                while (grids.Any(g => g.Comp.DirtyChunks.Count > 0) && batches < 100)
                {
                    var before = grids.Select(g => g.Comp.DirtyChunks.Count).ToArray();
                    pathfinding.Update(0);
                    var after = grids.Select(g => g.Comp.DirtyChunks.Count).ToArray();
                    Assert.That(before.Sum() - after.Sum(), Is.InRange(1, 8), "A tick must retain excess chunks in the queue");
                    for (var i = 0; i < grids.Count; i++)
                        Assert.That(before[i] - after[i], Is.InRange(0, 4), "A single grid cannot consume the full tick budget");
                    batches++;
                    if (batches == 4)
                        Assert.That(grids.All(g => g.Comp.DirtyChunks.Count < 33), Is.True, "All grids must receive service under sustained load");
                }
                Assert.That(grids.All(g => g.Comp.DirtyChunks.Count == 0), Is.True);
                TestContext.Out.WriteLine($"Drained 132 ordinary-grid chunks in {batches} bounded update batches.");
            }
            finally
            {
                entities.DeleteEntity(map);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReturningDirtyGridCannotStarvePersistentBacklog()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
#pragma warning disable RA0002 // Exercise real queue membership/cooldown changes in an isolated disposable workload.
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var pathfinding = entities.System<PathfindingSystem>();
            var map = maps.CreateMap(out var mapId);
            var workload = new HashSet<EntityUid>();
            Action<string, long, int> oneGridBudget = (phase, _, _) =>
            {
                if (phase == "npc.pathfinding.mesh_chunks")
                    Thread.Sleep(4); // Exceed the soft 2 ms budget after one real mesh batch.
            };
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var grid = maps.CreateGridEntity(mapId);
                    workload.Add(grid.Owner);
                    entities.EnsureComponent<GridPathfindingComponent>(grid.Owner);
                }
                // Use the scheduler's actual query order, independent of entity allocation order.
                var grids = new List<Entity<GridPathfindingComponent>>();
                var query = entities.AllEntityQueryEnumerator<GridPathfindingComponent>();
                while (query.MoveNext(out var uid, out var component))
                {
                    component.DirtyChunks.Clear();
                    if (workload.Contains(uid))
                        grids.Add((uid, component));
                }
                Assert.That(grids.Count, Is.EqualTo(3));
                for (var i = 0; i < grids.Count; i++)
                {
                    var component = grids[i].Comp;
                    component.NextUpdate = TimeSpan.Zero;
                    for (var chunk = 0; chunk < (i == 0 ? 1 : 100); chunk++)
                        component.DirtyChunks.Add(new Vector2i(chunk, i));
                }
                entities.DiagnosticWorkTiming += oneGridBudget;
                var serviced = new List<int>();
                for (var tick = 0; tick < 12; tick++)
                {
                    // A disappears from the filtered dirty set after service and returns.
                    // B/C remain dirty throughout. An index into the changing filtered list
                    // used to yield A,C,C,A,C,C..., never servicing B.
                    if (tick % 3 == 2 && grids[0].Comp.DirtyChunks.Count == 0)
                        grids[0].Comp.DirtyChunks.Add(new Vector2i(1000 + tick, 0));
                    var before = grids.Select(g => g.Comp.DirtyChunks.Count).ToArray();
                    pathfinding.Update(0);
                    var served = Enumerable.Range(0, 3)
                        .Where(i => grids[i].Comp.DirtyChunks.Count < before[i]).ToArray();
                    Assert.That(served.Length, Is.EqualTo(1), "The forced deadline allows exactly one grid batch");
                    serviced.Add(served.Single());
                    if (tick >= 3)
                    {
                        var window = serviced.Skip(serviced.Count - 4).ToArray();
                        Assert.That(window, Does.Contain(1), "Persistent grid B must not be starved by A returning");
                        Assert.That(window, Does.Contain(2), "Persistent grid C must also continue receiving service");
                    }
                }

                // A waiting cooldown entry must survive, and a deleted queue entry must
                // neither block the remaining work nor leave a stale component reference.
                grids[0].Comp.DirtyChunks.Clear();
                grids[0].Comp.DirtyChunks.Add(new Vector2i(2000, 0));
                grids[0].Comp.NextUpdate = TimeSpan.MaxValue;
                grids[1].Comp.DirtyChunks.Clear();
                grids[1].Comp.DirtyChunks.Add(new Vector2i(2000, 1));
                entities.DeleteEntity(grids[2].Owner);
                for (var i = 0; i < 4; i++)
                    pathfinding.Update(0);
                Assert.That(grids[1].Comp.DirtyChunks, Is.Empty);
                Assert.That(grids[0].Comp.DirtyChunks.Count, Is.EqualTo(1), "A cooldown must retain queued work");
                grids[0].Comp.NextUpdate = TimeSpan.Zero;
                pathfinding.Update(0);
                Assert.That(grids[0].Comp.DirtyChunks, Is.Empty, "The retained cooldown work must resume once eligible");
            }
            finally
            {
                entities.DiagnosticWorkTiming -= oneGridBudget;
                entities.DeleteEntity(map);
            }
        });
#pragma warning restore RA0002
        await pair.CleanReturnAsync();
    }
}
