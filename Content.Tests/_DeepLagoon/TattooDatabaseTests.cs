using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.Humanoid;
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
public sealed class TattooDatabaseTests
{
    [Test]
    public async Task SqliteUpgradeAndCharacterSavePreserveEditableLuminousInk()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            var previous = context.Database.GetMigrations().TakeWhile(id => id != "20261012090000_DeepLagoonTattoos").Last();
            await context.GetService<IMigrator>().MigrateAsync(previous);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color) VALUES ({user.UserId}, 0, '#FFFFFF')");
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        }
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var tattoo = new Content.Shared._DeepLagoon.Tattoos.Tattoo { NormalBitmap = TattooTests.Bitmap(), GlowBitmap = TattooTests.Bitmap(true), Direction = 2 };
        var profile = new HumanoidCharacterProfile().WithCharacterAppearance(new HumanoidCharacterAppearance { Tattoos = [tattoo] });
        await database.SaveCharacterSlotAsync(user, profile, 0);
        var loaded = await database.GetPlayerPreferencesAsync(user);
        var saved = (HumanoidCharacterProfile) loaded!.Characters[0];
        Assert.That(saved.Appearance.Tattoos.Single().ContentEquals(tattoo), Is.True);
        Assert.That(saved.Appearance.WithEyeColor(Robust.Shared.Maths.Color.Blue).Tattoos.Single().ContentEquals(tattoo), Is.True);
    }

    [Test]
    public void PostgresMigrationAddsOnlyTheTattooColumnAndMatchesTheModel()
    {
        var options = new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options;
        using var context = new PostgresServerDbContext(options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var previous = context.Database.GetMigrations().TakeWhile(id => id != "20261012090000_DeepLagoonTattoos").Last();
        var sql = context.GetService<IMigrator>().GenerateScript(previous, "20261012090000_DeepLagoonTattoos");
        Assert.That(sql, Does.Contain("ADD tattoos_json text"));
        Assert.That(sql, Does.Not.Contain("DROP TABLE"));
    }
}
