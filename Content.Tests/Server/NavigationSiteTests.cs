using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class NavigationSiteTests
{
    [Test]
    public void SignalsStayStableWhenRegionsAreAppendedOrReordered()
    {
        var sector = SectorGenerator.Generate("nav", 713, new SectorProfilePrototype());
        var original = NavigationSiteCatalog.Generate(sector).ToDictionary(s => s.Id);
        sector.Regions.Reverse();
        sector.Regions.Add(new SpaceRegion { Id = "appended", Kind = SpaceRegionKind.Dust, Radius = 300 });
        var expanded = NavigationSiteCatalog.Generate(sector).ToDictionary(s => s.Id);
        foreach (var (id, site) in original) Assert.That(expanded[id], Is.EqualTo(site));
        Assert.That(expanded.Count, Is.LessThanOrEqualTo(SectorGenerator.MaxRegions + SectorGenerator.MaxPlanets + 12));
    }

    [Test]
    public void RotatedEllipsesHaveNarrowCorridorsAndLocalShieldedPockets()
    {
        var region = new SpaceRegion { X = 900, Y = -300, Rotation = 1.2f, Radius = 500, Aspect = 0.4f };
        Vector2 At(float x, float y) => NavigationSiteCatalog.RegionPoint(region, new Vector2(x, y));
        Assert.That(NavigationSiteCatalog.InDustCorridor(region, At(-0.8f, 0), At(0.8f, 0.1f)), Is.True);
        Assert.That(NavigationSiteCatalog.InDustCorridor(region, At(-0.8f, 0), At(0.8f, 0.2f)), Is.False);
        Assert.That(NavigationSiteCatalog.InRadiationPocket(region, At(0.4f, 0)), Is.True);
        Assert.That(NavigationSiteCatalog.InRadiationPocket(region, At(0, 0)), Is.False);
    }

    [TestCase(0u)]
    [TestCase(713u)]
    [TestCase(uint.MaxValue)]
    public void PlasmaWindowsLastTwentySecondsAndForecastAgreesWithInteraction(uint seed)
    {
        var quiet = 0;
        for (var second = 0; second < 90; second++)
        {
            if (NavigationSiteCatalog.PlasmaQuiet(seed, second)) quiet++;
            var wait = NavigationSiteCatalog.QuietIn(seed, second);
            Assert.That(NavigationSiteCatalog.PlasmaQuiet(seed, second + wait), Is.True);
            Assert.That(NavigationSiteCatalog.PlasmaQuiet(seed, second + 90), Is.EqualTo(wait == 0));
        }
        Assert.That(quiet, Is.EqualTo(20));
    }

    [Test]
    public void RecoveryChoiceIsExclusiveAndSurvivesReopening()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"dl-signals-{Guid.NewGuid():N}.db");
        var sector = SectorGenerator.Generate("nav", 713, new SectorProfilePrototype());
        var site = NavigationSiteCatalog.Generate(sector)[0].Id;
        try
        {
            using (var first = new SectorStore(path))
            using (var second = new SectorStore(path))
            {
                first.GetOrCreate("crew", sector);
                Assert.That(first.TryRecordEncounter("crew", sector.Id, site, "data"), Is.True);
                Assert.That(second.TryRecordEncounter("crew", sector.Id, site, "cargo"), Is.False);
                Assert.That(first.TryRecordEncounter("crew", sector.Id, "invented", "data"), Is.False);
                Assert.That(first.TryRecordEncounter("other", sector.Id, site, "data"), Is.False);
            }
            using var reopened = new SectorStore(path);
            Assert.That(reopened.Encounters("crew", sector.Id), Is.EquivalentTo(new[] { site }));
            Assert.That(reopened.TryRecordEncounter("crew", sector.Id, site, "data"), Is.False);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
