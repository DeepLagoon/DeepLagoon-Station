using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Microsoft.Data.Sqlite;
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
    public void DerelictSlotsAreFiniteAndSurviveReopening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-derelicts-{Guid.NewGuid():N}.db");
        try
        {
            var definition = SectorGenerator.Generate("wrecks", 713, new SectorProfilePrototype());
            definition.Regions = new() { new SpaceRegion { Id = "stream", Kind = SpaceRegionKind.Asteroids, Radius = 2000 } };
            var limit = OrbitalDebrisSystem.DerelictLimit(definition.Seed, "stream");
            Assert.That(limit, Is.InRange(1, 3));
            using (var store = new SectorStore(path))
            {
                store.GetOrCreate("crew", definition);
                for (var slot = 0; slot < limit; slot++)
                {
                    Assert.That(store.TryRecordDerelict("crew", definition.Id, "stream", slot), Is.True);
                    Assert.That(store.TryRecordDerelict("crew", definition.Id, "stream", slot), Is.False);
                }
                Assert.That(store.TryRecordDerelict("crew", definition.Id, "stream", limit), Is.False);
                Assert.That(store.TryRecordDerelict("crew", definition.Id, "missing", 0), Is.False);
                Assert.That(store.TryRecordDerelict("other", definition.Id, "stream", 0), Is.False);
            }
            using var reopened = new SectorStore(path);
            Assert.That(reopened.DerelictSlots("crew", definition.Id, "stream"), Is.EqualTo((1 << limit) - 1));
            Assert.That(reopened.TryRecordDerelict("crew", definition.Id, "stream", 0), Is.False);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void ReopeningPreservesManifestWeatherEpochAndSurveyExpiry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-sector-{Guid.NewGuid():N}.db");
        try
        {
            var definition = SectorGenerator.Generate("sector/9", 99, new SectorProfilePrototype());
            var now = DateTimeOffset.UnixEpoch.AddDays(1);
            var region = definition.Regions[0].Id;
            using (var store = new SectorStore(path))
            {
                store.GetOrCreate("crew-a", definition);
                Assert.That(store.WeatherEpoch("crew-a", definition.Id, 42), Is.EqualTo(42));
                Assert.That(store.TrySurvey("crew-a", definition.Id, region, now), Is.True);
                Assert.That(store.TrySurvey("crew-a", definition.Id, "invented-region", now), Is.False);
                Assert.That(store.TrySurvey("crew-b", definition.Id, region, now), Is.False);
            }
            using var reopened = new SectorStore(path);
            var changed = SectorGenerator.Generate("sector/9", 123, new SectorProfilePrototype { RegionCount = 12 });
            Assert.That(reopened.GetOrCreate("crew-a", changed).Seed, Is.EqualTo(99));
            Assert.That(reopened.WeatherEpoch("crew-a", definition.Id, 99), Is.EqualTo(42));
            var expires = now.AddHours(5);
            var beforeExpiry = expires.AddMilliseconds(-1);
            Assert.That(reopened.Surveys("crew-a", definition.Id, beforeExpiry, out var next), Is.EqualTo(new[] { region }));
            Assert.That(next, Is.EqualTo(expires.ToUnixTimeMilliseconds()));
            Assert.That(reopened.TrySurvey("crew-a", definition.Id, region, beforeExpiry), Is.False);
            Assert.That(reopened.Surveys("crew-a", definition.Id, expires, out _), Is.Empty);
            Assert.That(reopened.TrySurvey("crew-a", definition.Id, region, expires), Is.True);
            Assert.That(reopened.GetOrCreate("crew-b", changed).Seed, Is.EqualTo(123));
            Assert.That(reopened.Surveys("crew-b", definition.Id, now, out _), Is.Empty);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Test]
    public void LegacySurveysAreMigratedAsExpiredAndCanBeResurveyed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-sector-legacy-{Guid.NewGuid():N}.db");
        try
        {
            var definition = SectorGenerator.Generate("legacy", 99, new SectorProfilePrototype());
            var region = definition.Regions[0].Id;
            var now = DateTimeOffset.UnixEpoch.AddDays(1);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sector_surveys (
                        campaign TEXT NOT NULL, sector TEXT NOT NULL, region TEXT NOT NULL,
                        PRIMARY KEY (campaign, sector, region)
                    );
                    CREATE TABLE sector_round_surveys (
                        campaign TEXT NOT NULL, round_id INTEGER NOT NULL, sector TEXT NOT NULL, region TEXT NOT NULL,
                        PRIMARY KEY (campaign, round_id, sector, region)
                    );
                    INSERT INTO sector_surveys VALUES ('crew', 'legacy', $region);
                    INSERT INTO sector_round_surveys VALUES ('crew', 42, 'legacy', $region);
                    """;
                command.Parameters.AddWithValue("$region", region);
                command.ExecuteNonQuery();
            }

            using (var store = new SectorStore(path))
            {
                store.GetOrCreate("crew", definition);
                Assert.That(store.Surveys("crew", definition.Id, now, out _), Is.Empty);
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT expires FROM sector_surveys";
                Assert.That(command.ExecuteScalar(), Is.EqualTo(0L));
                command.CommandText = "SELECT round_id FROM sector_round_surveys";
                Assert.That(command.ExecuteScalar(), Is.EqualTo(42L));
                Assert.That(store.TrySurvey("crew", definition.Id, region, now), Is.True);
            }

            using var reopened = new SectorStore(path);
            Assert.That(reopened.Surveys("crew", definition.Id, now, out var expiry), Is.EqualTo(new[] { region }));
            Assert.That(expiry, Is.EqualTo(now.AddHours(5).ToUnixTimeMilliseconds()));
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
            var now = DateTimeOffset.UnixEpoch.AddDays(1);
            Assert.That(first.TrySurvey("crew", sector.Id, sector.Regions[0].Id, now.AddHours(-5)), Is.True);
            var results = await Task.WhenAll(
                Task.Run(() => first.TrySurvey("crew", "test", sector.Regions[0].Id, now)),
                Task.Run(() => second.TrySurvey("crew", "test", sector.Regions[0].Id, now)));
            Assert.That(results.Count(success => success), Is.EqualTo(1));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Test]
    public void AppendOnlyUpgradePreservesDiscoveriesAndRejectsStaleOverwrite()
    {
        using var store = new SectorStore(":memory:");
        var old = store.GetOrCreate("crew", SectorGenerator.Generate("old", 51, new SectorProfilePrototype { RegionCount = 36 }));
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        store.TrySurvey("crew", old.Id, old.Regions[0].Id, now);
        var expanded = SectorGenerator.ExpandRegions(old, new SectorProfilePrototype { RegionCount = 72 });
        var added = expanded.Regions.Skip(old.Regions.Count).ToList();
        Assert.That(store.AppendRegions("crew", old, added).Regions, Has.Count.EqualTo(72));
        Assert.That(store.AppendRegions("crew", old, added).Regions, Has.Count.EqualTo(72));
        Assert.That(store.Surveys("crew", old.Id, now, out _), Is.EqualTo(new[] { old.Regions[0].Id }));
        Assert.That(store.Load("crew", old.Id).Planets[0].AppearanceSeed, Is.EqualTo(old.Planets[0].AppearanceSeed));
    }

    [Test]
    public void WeatherChangesAllRegionsWithoutMovingSignalsOrChangingIdentity()
    {
        var baseline = new SectorDefinition
        {
            Id = "weather", Seed = 713,
            Regions = Enum.GetValues<SpaceRegionKind>().Select(kind => new SpaceRegion
            {
                Id = kind.ToString(), Kind = kind, X = 5000, Y = 1000, Radius = 700, Aspect = 0.7f, Rotation = 0.9f,
            }).ToList(),
        };

        Assert.That(SectorWeather.At(baseline, SectorWeather.Sample(599)), Is.SameAs(baseline));
        Assert.That(SectorWeather.Sample(1199), Is.EqualTo(690));
        var middle = SectorWeather.At(baseline, SectorWeather.Sample(645));
        var final = SectorWeather.At(baseline, SectorWeather.Sample(690));
        var later = SectorWeather.At(baseline, SectorWeather.Sample(1290));
        for (var i = 0; i < baseline.Regions.Count; i++)
        {
            var original = baseline.Regions[i];
            var changed = final.Regions[i];
            var before = SpaceRegionGeometry.World(original, new Vector2(0.4f, 0));
            var after = SpaceRegionGeometry.World(changed, new Vector2(0.4f, 0));
            Assert.That(changed.Id, Is.EqualTo(original.Id));
            Assert.That(changed.Kind, Is.EqualTo(original.Kind));
            Assert.That(Vector2.Distance(before, after), Is.LessThan(0.002f));
            Assert.That(Vector2.Distance(new Vector2(changed.X, changed.Y), new Vector2(original.X, original.Y)), Is.InRange(2, 80));
            Assert.That(middle.Regions[i].Radius, Is.EqualTo((original.Radius + changed.Radius) / 2).Within(0.001));
            Assert.That(later.Regions[i].Radius, Is.Not.EqualTo(changed.Radius));
        }
    }

    [Test]
    public void PlasmaSurgesAndQuietWindowRepeatInTheSamePhase()
    {
        Assert.That(PlasmaWeather.Strength(0, 0), Is.GreaterThan(PlasmaWeather.Strength(0, 3.5)));
        Assert.That(PlasmaWeather.Quiet(0, 69), Is.False);
        Assert.That(PlasmaWeather.QuietIn(0, 69), Is.EqualTo(1));
        Assert.That(PlasmaWeather.Quiet(0, 70), Is.True);
        Assert.That(PlasmaWeather.Strength(0, 89), Is.EqualTo(0.15f));
        Assert.That(PlasmaWeather.Strength(0, 90), Is.EqualTo(PlasmaWeather.Strength(0, 0)));
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
