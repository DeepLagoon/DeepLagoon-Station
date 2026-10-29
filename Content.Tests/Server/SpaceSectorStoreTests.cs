using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class SpaceSectorStoreTests
{
    [OneTimeSetUp]
    public void Sqlite()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }

    [Test]
    public void ReopeningPreservesManifestAndIdempotentSurveysAcrossCampaigns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-sector-{Guid.NewGuid():N}.db");
        try
        {
            var definition = SectorGenerator.Generate("sector/9", 99, new SectorProfilePrototype());
            using (var store = new SectorStore(path))
            {
                store.GetOrCreate("crew-a", definition);
                Assert.That(store.TrySurvey("crew-a", definition.Id, definition.Regions[0].Id), Is.True);
                Assert.That(store.TrySurvey("crew-a", definition.Id, definition.Regions[0].Id), Is.False);
                Assert.That(store.TrySurvey("crew-a", definition.Id, "invented-region"), Is.False);
                Assert.That(store.TrySurvey("crew-b", definition.Id, definition.Regions[0].Id), Is.False);
            }
            using var reopened = new SectorStore(path);
            var changed = SectorGenerator.Generate("sector/9", 123, new SectorProfilePrototype { RegionCount = 12 });
            Assert.That(reopened.GetOrCreate("crew-a", changed).Seed, Is.EqualTo(99));
            Assert.That(reopened.Surveys("crew-a", definition.Id), Has.Count.EqualTo(1));
            Assert.That(reopened.GetOrCreate("crew-b", changed).Seed, Is.EqualTo(123));
            Assert.That(reopened.Surveys("crew-b", definition.Id), Is.Empty);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Test]
    public async Task TwoConnectionsCannotAwardTheSameSurveyTwice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-sector-concurrent-{Guid.NewGuid():N}.db");
        try
        {
            using var first = new SectorStore(path);
            using var second = new SectorStore(path);
            var sector = SectorGenerator.Generate("test", 2, new SectorProfilePrototype());
            first.GetOrCreate("crew", sector);
            var results = await Task.WhenAll(
                Task.Run(() => first.TrySurvey("crew", "test", sector.Regions[0].Id)),
                Task.Run(() => second.TrySurvey("crew", "test", sector.Regions[0].Id)));
            Assert.That(results.Count(success => success), Is.EqualTo(1));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Test]
    public void UnknownVersionsAndNonFiniteGeometryFailClosed()
    {
        using var store = new SectorStore(":memory:");
        Assert.Throws<InvalidDataException>(() => store.GetOrCreate("crew", new SectorDefinition { Id = "future", Version = 999 }));
        var malformed = new SectorDefinition { Id = "bad", Regions = new() { new SpaceRegion { Id = "r", Radius = float.NaN } } };
        Assert.Throws<InvalidDataException>(() => store.GetOrCreate("crew", malformed));
        Assert.Throws<ArgumentException>(() => store.Load("", "sector"));
    }
}
