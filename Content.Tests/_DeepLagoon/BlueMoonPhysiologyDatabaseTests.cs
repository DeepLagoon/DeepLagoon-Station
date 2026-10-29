using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.Preferences;
using Content.Shared._DeepLagoon.InteractionPanel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class BlueMoonPhysiologyDatabaseTests
{
    [Test]
    public async Task UpgradeAndProfileRoundTripPreservePhysiologyAndOptIns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261026010000_LagoonEconomy");
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color) VALUES ({user.UserId}, 0, '#FFFFFF')");
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        }
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var physiology = new BlueMoonPreferences { Tolerance = 175, Potency = -1, FluidGrowth = true, Pregnancy = true,
            ReceiveFluids = true, Fertility = 75, Virility = 90, Penis = BlueMoonSensitivity.Hypersensitive };
        await database.SaveCharacterSlotAsync(user, new HumanoidCharacterProfile().WithArousalPreferences(physiology), 0);
        await database.SaveCharacterSlotAsync(user, new HumanoidCharacterProfile(), 1);
        var loaded = await database.GetPlayerPreferencesAsync(user);
        Assert.That(((HumanoidCharacterProfile)loaded!.Characters[0]).ArousalPreferences, Is.EqualTo(physiology));
        Assert.That(((HumanoidCharacterProfile)loaded.Characters[1]).ArousalPreferences.Pregnancy, Is.False);
        Assert.That(((HumanoidCharacterProfile)loaded.Characters[1]).ArousalPreferences.FluidGrowth, Is.False);
    }

    [Test]
    public void PostgresMigrationIsAdditiveAndMatchesModel()
    {
        using var context = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var sql = context.GetService<IMigrator>().GenerateScript("20261026010000_LagoonEconomy", "20261029190000_BlueMoonPhysiology");
        Assert.That(sql, Does.Contain("ADD arousal_preferences_json text NOT NULL DEFAULT '{}'"));
        Assert.That(sql, Does.Not.Contain("DROP TABLE"));
    }
}
