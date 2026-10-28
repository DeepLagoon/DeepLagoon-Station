using System;
using System.IO;
using System.Text.Json;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ApartmentAtmosphereSettingsTests
{
    [Test]
    public void LegacyLayoutsUseStandardComposition()
    {
        var delta = JsonSerializer.Deserialize<ApartmentDelta>("{\"SchemaVersion\":1}");
        Assert.That(delta!.OxygenPercent, Is.EqualTo(21));
    }

    [Test]
    public void CompositionSurvivesReopenAndTemplateSwitch()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"dl-apartment-air-{Guid.NewGuid():N}.db");
        var owner = Guid.NewGuid();
        var first = new ApartmentTemplatePrototype { ID = "first" };
        var second = new ApartmentTemplatePrototype { ID = "second" };
        try
        {
            using (var store = new ApartmentLayoutStore(path))
            {
                var delta = store.Select(owner, first);
                delta.OxygenPercent = 35;
                var revision = delta.Revision++;
                Assert.That(store.Save(owner, delta, revision), Is.True);
                Assert.That(store.Select(owner, second).OxygenPercent, Is.EqualTo(21));
                Assert.That(store.Select(owner, first).OxygenPercent, Is.EqualTo(35));
            }
            using var reopened = new ApartmentLayoutStore(path);
            Assert.That(reopened.Load(owner)!.OxygenPercent, Is.EqualTo(35));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
