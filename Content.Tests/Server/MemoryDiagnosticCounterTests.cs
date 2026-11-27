using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Currency;
using Content.Server._DeepLagoon.Persistence;
using Content.Server.NPC.Pathfinding;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class MemoryDiagnosticCounterTests
{
    [Test]
    public void ScalarProvidersDoNotInitializeStorageOrQueryEntities()
    {
        var values = new Dictionary<string, long>();
        new ApartmentSystem().CollectMemoryDiagnostics((name, value) => values.Add(name, value));
        new LagoonCoinSystem().CollectMemoryDiagnostics((name, value) => values.Add(name, value));
        new PathfindingSystem().CollectMemoryDiagnostics((name, value) => values.Add(name, value));
        Assert.Multiple(() =>
        {
            Assert.That(values["apartments.writer_pending"], Is.Zero);
            Assert.That(values["apartments.snapshot_gates"], Is.Zero);
            Assert.That(values["lc.pending_rewards"], Is.Zero);
            Assert.That(values["lc.outbox_writer_pending"], Is.Zero);
            Assert.That(values["navigation.dirty_grids"], Is.Zero);
            Assert.That(values["navigation.request_capacity"], Is.GreaterThanOrEqualTo(values["navigation.requests"]));
        });
    }

    [Test]
    public async Task PersistenceQueueCounterDoesNotChangeOrderingOrReceiptSemantics()
    {
        using var writer = new SerialPersistenceQueue();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = writer.Enqueue(() => { entered.Set(); release.Wait(); return 1; });
        try
        {
            Assert.That(entered.Wait(5000), Is.True);
            var second = writer.Enqueue(() => 2);
            var third = writer.Enqueue(() => 3);
            Assert.That(writer.PendingCount, Is.EqualTo(2));
            Assert.That(second.IsCompleted, Is.False);
            release.Set();
            Assert.That(await first, Is.EqualTo(1));
            Assert.That(await second, Is.EqualTo(2));
            Assert.That(await third, Is.EqualTo(3));
            Assert.That(writer.PendingCount, Is.Zero);
        }
        finally { release.Set(); }
    }
}
