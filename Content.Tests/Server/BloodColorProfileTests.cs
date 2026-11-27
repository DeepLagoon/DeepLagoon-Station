using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Content.Tests.Server;

[TestFixture]
public sealed class BloodColorProfileTests
{
    [Test]
    public void BloodColorSurvivesProfileCopiesAndInvalidValuesKeepSpeciesDefault()
    {
        var original = new HumanoidCharacterProfile();
        var colored = original.WithBloodColor("#123456");
        Assert.That(original.BloodColor, Is.Empty);
        Assert.That(colored.MemberwiseEquals(original), Is.False);
        Assert.That(new HumanoidCharacterProfile(colored).BloodColor, Is.EqualTo(colored.BloodColor));
        Assert.That(colored.WithName("Other").BloodColor, Is.EqualTo(colored.BloodColor));
        foreach (var invalid in new[] { "", "invalid", "#12345600", "#xyzxyz" })
            Assert.That(colored.WithBloodColor(invalid).BloodColor, Is.Empty);
    }

    [Test]
    public async Task BloodColorPersistsPerCharacterSlot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        await using (var db = new SqliteServerDbContext(options))
        {
            await db.Database.MigrateAsync();
            Assert.That(db.Database.HasPendingModelChanges(), Is.False);
            db.Preference.Add(new Preference { UserId = user.UserId, AdminOOCColor = "#000000" });
            db.Preference.Add(new Preference { UserId = other.UserId, AdminOOCColor = "#000000" });
            await db.SaveChangesAsync();
        }
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await database.SaveCharacterSlotAsync(user, new HumanoidCharacterProfile().WithBloodColor("#123456"), 0);
        await database.SaveCharacterSlotAsync(user, new HumanoidCharacterProfile().WithBloodColor("#654321"), 1);
        await database.SaveCharacterSlotAsync(other, new HumanoidCharacterProfile(), 0);
        var loaded = (await database.GetPlayerPreferencesAsync(user))!;
        Assert.That(((HumanoidCharacterProfile) loaded.Characters[0]).BloodColor, Is.EqualTo("#123456"));
        Assert.That(((HumanoidCharacterProfile) loaded.Characters[1]).BloodColor, Is.EqualTo("#654321"));
        Assert.That(((HumanoidCharacterProfile) (await database.GetPlayerPreferencesAsync(other))!.Characters[0]).BloodColor, Is.Empty);
    }

    [Test]
    public async Task LegacyProfileUpgradeAndSupportedRollbackPreserveUnrelatedCharacterData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var db = new SqliteServerDbContext(options))
        {
            var previous = db.Database.GetMigrations().TakeWhile(id => id != "20261126120000_CharacterBloodColor").Last();
            // Start from the actual old schema, not a downgrade of a newly saved profile.
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO preference(preference_id,user_id,selected_character_slot,admin_ooc_color,mono_coins)
                VALUES(1,{user.UserId},0,'#000000',42)
                """);
            var legacy = new Dictionary<string, object>
            {
                ["profile_id"] = 1, ["preference_id"] = 1, ["slot"] = 0,
                ["char_name"] = "Legacy Character", ["age"] = 25,
                ["sex"] = "Male", ["gender"] = "Male", ["species"] = "Human",
                ["hair_name"] = "", ["hair_color"] = "#000000",
                ["facial_hair_name"] = "", ["facial_hair_color"] = "#000000",
                ["eye_color"] = "#000000", ["skin_color"] = "#000000",
                ["pref_unavailable"] = 0, ["spawn_priority"] = 0, ["bank_balance"] = 321,
                ["company"] = "None", ["flavor_text"] = "Legacy description", ["flags"] = "[]",
                ["height"] = 1f, ["width"] = 1f, ["ooc_notes"] = "", ["headshot_id"] = "",
                ["tattoos_json"] = "[]", ["body_features_json"] = "[]", ["plantigrade_legs"] = 0,
                ["custom_species_name"] = "", ["speech_bark_settings_json"] = "{}",
                ["arousal_preferences_json"] = "{}", ["friendly_consent"] = 1,
                ["hugs_consent"] = 1, ["playful_consent"] = 1
            };
            // Check the actual old schema, reporting every missing required value
            // at once instead of relying on defaults lost in older table rebuilds.
            var missing = new List<string>();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(profile)";
                await using var columns = await schema.ExecuteReaderAsync();
                while (await columns.ReadAsync())
                    if (columns.GetInt64(3) != 0 && columns.IsDBNull(4) && columns.GetInt64(5) == 0 &&
                        !legacy.ContainsKey(columns.GetString(1)))
                        missing.Add(columns.GetString(1));
            }
            Assert.That(missing, Is.Empty, "Legacy seed must supply all required columns.");
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO profile (" + string.Join(",", legacy.Keys) + ") VALUES (" +
                    string.Join(",", legacy.Keys.Select(column => "$" + column)) + ")";
                foreach (var (column, value) in legacy)
                    insert.Parameters.AddWithValue("$" + column, value);
                await insert.ExecuteNonQueryAsync();
            }
            await db.Database.MigrateAsync();
            Assert.That(db.Database.HasPendingModelChanges(), Is.False);
            var upgraded = await db.Profile.SingleAsync();
            Assert.That(upgraded.BloodColor, Is.Empty);
            Assert.That(upgraded.CharacterName, Is.EqualTo("Legacy Character"));
            Assert.That(upgraded.BankBalance, Is.EqualTo(321));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            Assert.That(await db.Profile.Select(profile => profile.CharacterName).SingleAsync(), Is.EqualTo("Legacy Character"));
            await db.Database.MigrateAsync();
        }
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        Assert.That(((HumanoidCharacterProfile) (await database.GetPlayerPreferencesAsync(user))!.Characters[0]).BloodColor, Is.Empty);
    }

    [Test]
    public void PostgresBloodColorMigrationMatchesModelAndIsAdditive()
    {
        using var db = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        Assert.That(db.Database.HasPendingModelChanges(), Is.False);
        var previous = db.Database.GetMigrations().TakeWhile(id => id != "20261126120000_CharacterBloodColor").Last();
        var script = db.GetService<IMigrator>().GenerateScript(previous, "20261126120000_CharacterBloodColor");
        Assert.That(script, Does.Contain("ADD blood_color text NOT NULL DEFAULT ''"));
        Assert.That(script, Does.Not.Contain("DROP TABLE"));
    }
}
