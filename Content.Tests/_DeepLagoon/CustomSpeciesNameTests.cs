using System;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.Preferences;
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
public sealed class CustomSpeciesNameTests
{
    [Test]
    public async Task MigratesExistingSchemaAndPreservesCosmeticSpeciesName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261029190000_BlueMoonPhysiology");
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color) VALUES ({user.UserId}, 0, '#FFFFFF')");
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        }

        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
            .WithCustomSpeciesName("  Лунный шакал\n  ")
            .WithName("Test Character");
        await database.SaveCharacterSlotAsync(user, profile, 0);
        var preferences = await database.GetPlayerPreferencesAsync(user);
        var saved = (HumanoidCharacterProfile) preferences!.Characters[0];
        Assert.Multiple(() =>
        {
            Assert.That(saved.CustomSpeciesName, Is.EqualTo("Лунный шакал"));
            Assert.That(saved.Species.Id, Is.EqualTo("Human"));
        });
    }

    [Test]
    public void PostgresMigrationAddsCosmeticSpeciesName()
    {
        using var context = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var sql = context.GetService<IMigrator>().GenerateScript("20261029190000_BlueMoonPhysiology", "20261105120000_CustomSpeciesName");
        Assert.That(sql, Does.Contain("ADD custom_species_name text NOT NULL DEFAULT ''"));
    }
}
