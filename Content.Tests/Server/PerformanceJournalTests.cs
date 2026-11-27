using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Diagnostics;
using Content.Shared._DeepLagoon.Performance;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture, NonParallelizable]
public sealed class PerformanceJournalTests
{
    private string _directory;
    [SetUp]
    public void Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _directory = Path.Combine(Path.GetTempPath(), "performance-journal-" + Guid.NewGuid());
    }
    [TearDown]
    public void Cleanup()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory)) File.Delete(path);
        Directory.Delete(_directory);
    }
    private static PerformanceMonitorSample Sample(int at, double tick = 10, double gc = 0)
        => new(1700000000 + at, 5, 30, 1024, 1500, 3, gc, tick, 4, null, 2, 30, 1)
        { Entities = 4000 + at, PvsWaitMs = .5, PvsSerializeMs = .7 };

    [Test]
    public async Task RoundFilesSurviveReopeningAndRangeQueriesRetainMeasuredCpuAndGcPeaks()
    {
        await using (var journal = new PerformanceJournal(_directory))
        {
            for (var i = 0; i < 5000; i++)
            {
                Assert.That(journal.Record(213, Sample(i, i == 2742 ? 180 : 10, i == 1204 ? 350 : 0)), Is.True);
                if (i % 100 == 99) await journal.FlushAsync();
            }
            Assert.That(journal.Report(213, 1700004000, "tick", "Server tick timing summary: tick_avg_ms=10"), Is.True);
            Assert.That(journal.Record(212, Sample(100)), Is.True);
            await journal.FlushAsync();
        }
        Assert.That(File.Exists(Path.Combine(_directory, "round-000213.db")), Is.True);
        Assert.That(File.Exists(Path.Combine(_directory, "round-000212.db")), Is.True);
        await using var reopened = new PerformanceJournal(_directory);
        var all = await reopened.Read(213, 0, 0);
        Assert.That(all.Count, Is.EqualTo(5000));
        Assert.That(all.Samples.Length, Is.LessThanOrEqualTo(1100));
        Assert.That(all.Samples.Any(sample => sample.TickMs == 180), Is.True);
        Assert.That(all.Samples.Any(sample => sample.GcPauseMs == 350), Is.True);
        Assert.That(all.Samples[^1].Entities, Is.EqualTo(8999));
        Assert.That(all.Reports.Single().Line, Does.Contain("tick_avg_ms=10"));
        var recent = await reopened.Read(213, 120, 0);
        Assert.That(recent.Samples.All(sample => sample.Time >= 1700004879), Is.True);
        Assert.That((await reopened.Read(212, 0, 0)).Count, Is.EqualTo(1));
        Assert.That(reopened.Report(213, 0, "tick", new string('x', 32769)), Is.False);
    }

    [Test]
    public async Task DiskFailureStaysOnTheWorkerAndIsVisibleInsteadOfCrashingTheTick()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked"); File.WriteAllText(blocked, "file instead of directory");
        await using var journal = new PerformanceJournal(blocked);
        Assert.That(journal.Record(1, Sample(1)), Is.True);
        await journal.FlushAsync();
        Assert.That(journal.Status, Does.Contain("Журнал недоступен"));
        Assert.ThrowsAsync<IOException>(async () => await journal.Read(1, 120, 0));
    }
}
