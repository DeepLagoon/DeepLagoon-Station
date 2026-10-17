using System;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Tests.Server;

[TestFixture]
public sealed class UnequippedSlotsDatabaseTest
{
    [Test]
    public void CloningAndEqualityIncludeEmptySlotsWithoutChangingTheOriginal()
    {
        var original = new RoleLoadout("JobContractor");
        var changed = original.Clone();
        changed.UnequippedSlots.Add("jumpsuit");
        Assert.That(original.UnequippedSlots, Is.Empty);
        Assert.That(changed.Equals(original), Is.False);
        Assert.That(changed.Clone().Equals(changed), Is.True);
    }

    [Test]
    public async Task SqliteUpgradeAndProfileRoundTripKeepRemovedEquipmentSlots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261008210000_EditorItemCustomizations");
            // Seed the historical schema without using a model that already contains later columns.
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color) VALUES ({user.UserId}, 0, '#FFFFFF')");
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        }
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var role = new RoleLoadout("JobContractor") { UnequippedSlots = new() { "jumpsuit", "shoes", "id", "back", "ears" } };
        var profile = new HumanoidCharacterProfile().WithLoadout(role);
        await database.SaveCharacterSlotAsync(user, profile, 0);
        var loaded = await database.GetPlayerPreferencesAsync(user);
        var saved = (HumanoidCharacterProfile)loaded!.Characters[0];
        Assert.That(saved.Loadouts[role.Role].UnequippedSlots, Is.EquivalentTo(role.UnequippedSlots));
    }

    [Test]
    public void PostgresMigrationOnlyAddsTheNullableSlotColumn()
    {
        var options = new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options;
        using var context = new PostgresServerDbContext(options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var sql = context.GetService<IMigrator>().GenerateScript("20261008210000_EditorItemCustomizations", "20261009160000_UnequippedLoadoutSlots");
        Assert.That(sql, Does.Contain("ADD unequipped_slots text"));
        Assert.That(sql, Does.Not.Contain("DROP TABLE"));
    }
}
