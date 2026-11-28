using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.ShuttleParking;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingSnapshotBudgetTests
{
    [Test]
    public async Task SnapshotYieldsRestoresTemporaryStateAndCancellationRemovesClone()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var map = await pair.CreateTestMap();
        IEnumerator steps = null;
        EntityUid source = map.Grid;
        object originals = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ParkingSystem);
        var phase = type.GetField("_savePhase", flags)!;
        var maxMs = 0.0;
        var count = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var parking = em.System<ParkingSystem>();
            em.System<SharedMapSystem>().SetPaused(map.MapId, true);
            parking.RegisterPurchasedShip(source, Guid.NewGuid(), 1000, "test", "Captain");
            for (var i = 0; i < 150; i++)
                em.SpawnEntity("Crowbar", new EntityCoordinates(source, new Vector2(i % 16, i / 16)));
            originals = em.GetComponent<ParkingShuttleComponent>(source).OriginalItems;
            steps = ((IEnumerable)type.GetMethod("FilteredSnapshotSteps", flags)!.Invoke(parking, [source, true])!).GetEnumerator();
        });
        var loaded = false;
        try
        {
            for (; count < 1000 && !loaded; count++)
            {
                await pair.Server.WaitAssertion(() =>
                {
                    var em = pair.Server.EntMan;
                    var parking = em.System<ParkingSystem>();
                    var clock = Stopwatch.StartNew();
                    Assert.That(steps.MoveNext(), Is.True);
                    maxMs = Math.Max(maxMs, clock.Elapsed.TotalMilliseconds);
                    Assert.That(em.EntityExists(source), Is.True);
                    Assert.That(em.GetComponent<ParkingShuttleComponent>(source).OriginalItems, Is.SameAs(originals));
                    loaded = (string)phase.GetValue(parking)! == "load_clone";
                });
                await pair.RunTicksSync(1);
            }
            Assert.That(loaded, Is.True);
            Assert.That(count, Is.GreaterThan(3), "The entity snapshot must span multiple simulation updates.");
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                Assert.That(em.EntityQuery<ParkingShuttleComponent>(true).Count(), Is.GreaterThan(1));
                ((IDisposable)steps).Dispose();
                Assert.That(em.EntityQuery<ParkingShuttleComponent>(true).Select(comp => comp.Owner), Is.EquivalentTo(new[] { source }));
                Assert.That(em.GetComponent<ParkingShuttleComponent>(source).OriginalItems, Is.SameAs(originals));
                em.DeleteEntity(map.MapUid);
            });
            TestContext.Out.WriteLine($"Parking staged preparation: {count} advances, maximum measured advance {maxMs:F3} ms (includes atomic grid serialization/load and GC; not a hard timing assertion).");
        }
        finally { await pair.Server.WaitPost(() => ((IDisposable)steps).Dispose()); }
        await pair.CleanReturnAsync();
    }
}
