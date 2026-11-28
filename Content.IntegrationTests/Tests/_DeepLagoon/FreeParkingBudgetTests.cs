using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class FreeParkingBudgetTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task OneGlobalBudgetFor120ParkedShipsAndCancellationRemovesQueuedWork()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        var steps = 0; var disposed = 0; var grids = new List<EntityUid>(); var cancellations = new List<Task>();
        var startup = new List<Task>();
        await pair.Server.WaitPost(() =>
        {
            var parking = pair.Server.EntMan.System<ParkingSystem>();
            foreach (var name in new[] { "_freeParkingIndex", "_freeSaleIndex" })
                if (typeof(ParkingSystem).GetField(name, Private)!.GetValue(parking) is Task index) startup.Add(index);
        });
        await Task.WhenAll(startup); // Observe the real startup workers before isolating the scheduler fixture.
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan; var parking = em.System<ParkingSystem>(); var type = typeof(ParkingSystem);
            type.GetMethod("StopFreeParking", Private)!.Invoke(parking, [false]);
            type.GetField("_freeParkingIndex", Private)!.SetValue(parking, null);
            type.GetField("_freeSaleScanRequested", Private)!.SetValue(parking, false);
            Assert.That(type.GetField("_saveWork", Private)!.GetValue(parking), Is.Null);
            Assert.That(type.GetField("_freeParkingRecall", Private)!.GetValue(parking), Is.Null);
            em.System<SharedMapSystem>().SetPaused(map.MapId, true);
            for (var i = 0; i < 120; i++)
            {
                var grid = em.System<SharedMapSystem>().CreateGridEntity(map.MapId);
                em.System<SharedMapSystem>().SetTile(grid, Vector2i.Zero, map.Tile.Tile);
                var ship = em.EnsureComponent<ParkingShuttleComponent>(grid.Owner);
                ship.Identity = Guid.NewGuid(); ship.OwnerAccount = Guid.NewGuid(); ship.Parked = true;
                ship.ParkingCharacter = 0; ship.ParkingProfile = 42; ship.OriginFaction = "Frontier";
                grids.Add(grid.Owner);
                type.GetMethod("QueueFreeParkingSnapshot", Private)!.Invoke(parking, [grid.Owner]);
            }
            var captures = (IDictionary)type.GetField("_freeParkingCaptures", Private)!.GetValue(parking)!;
            Assert.That(captures.Count, Is.EqualTo(120));
            foreach (var capture in captures.Values)
            {
                var snapshot = (FreeParkingSnapshot)capture!.GetType().GetField("Snapshot")!.GetValue(capture)!;
                Assert.That(snapshot.Category, Is.EqualTo(-1), "Queueing a free recovery must not synchronously inspect a hull or read the vessel catalog.");
                var field = capture.GetType().GetField("Steps")!;
                var iterator = typeof(FreeParkingBudgetTests).GetMethod(nameof(CountSteps), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(field.FieldType.GetGenericArguments()[0]).Invoke(null, [new Action(() => steps++), new Action(() => disposed++)]);
                field.SetValue(capture, iterator);
            }
            type.GetMethod("UpdateFreeParking", Private)!.Invoke(parking, null);
            Assert.That(steps, Is.InRange(1, 16), "The cap belongs to the whole server, never to each parked ship.");
            Assert.That(type.GetField("_freeParkingCapture", Private)!.GetValue(parking), Is.Not.Null);
            var queue = type.GetField("_freeParkingQueue", Private)!.GetValue(parking)!;
            Assert.That((int)queue.GetType().GetProperty("Count")!.GetValue(queue)!, Is.EqualTo(119));
            foreach (var grid in grids)
                cancellations.Add((Task)type.GetMethod("CancelFreeParkingSnapshot", Private)!.Invoke(parking, [grid])!);
            Assert.That(captures.Count, Is.Zero);
            Assert.That((int)queue.GetType().GetProperty("Count")!.GetValue(queue)!, Is.Zero, "Cancelled queued ships must not leave stale queue nodes.");
            Assert.That(disposed, Is.EqualTo(1), "Only one iterator was entered, and it was promptly disposed.");
        });
        await PoolManager.WaitUntil(pair.Server, () => cancellations.All(task => task.IsCompleted), pair.SecondsToTicks(60));
        await Task.WhenAll(cancellations);
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.CleanReturnAsync();
    }

    private static IEnumerator<T> CountSteps<T>(Action step, Action disposed)
    {
        try { while (true) { step(); yield return default!; } }
        finally { disposed(); }
    }
}
