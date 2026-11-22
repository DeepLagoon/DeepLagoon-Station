using Content.Server._DeepLagoon.CrashRecovery;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class CrashRecoveryWorkQueueTests
{
    [Test]
    public void RemovingDeletedWorkReleasesCapacityAndPreservesReinsertedWork()
    {
        var queue = new RecoveryWorkQueue<int>();
        Assert.That(queue.Enqueue(1, 3), Is.True);
        Assert.That(queue.Enqueue(2, 3), Is.True);
        Assert.That(queue.Enqueue(3, 3), Is.True);
        Assert.That(queue.Enqueue(4, 3), Is.False);
        Assert.That(queue.Remove(2), Is.True);
        Assert.That(queue.Enqueue(2, 3), Is.True);
        Assert.That(queue.TryDequeue(out var first), Is.True);
        Assert.That(first, Is.EqualTo(1));
        Assert.That(queue.TryDequeue(out var second), Is.True);
        Assert.That(second, Is.EqualTo(3));
        Assert.That(queue.TryDequeue(out var third), Is.True);
        Assert.That(third, Is.EqualTo(2));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void SeventyThousandDeletedEntriesDoNotLeaveAHiddenQueueBacklog()
    {
        var queue = new RecoveryWorkQueue<int>();
        for (var i = 0; i < 70000; i++)
        {
            Assert.That(queue.Enqueue(i, 1), Is.True);
            Assert.That(queue.Remove(i), Is.True);
        }
        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.TryDequeue(out _), Is.False);
        Assert.That(queue.Enqueue(70001, 1), Is.True);
        Assert.That(queue.TryDequeue(out var last), Is.True);
        Assert.That(last, Is.EqualTo(70001));
    }
}
