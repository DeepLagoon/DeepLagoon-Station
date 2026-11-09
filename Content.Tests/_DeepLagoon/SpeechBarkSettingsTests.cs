using System;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Speech;
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
public sealed class SpeechBarkSettingsTests
{
    [Test]
    public void VoiceSettingsAreBoundedAndCopiedWithTheCharacter()
    {
        var profile = new HumanoidCharacterProfile();
        var voice = new SpeechBarkSettings
        {
            Voice = new string('x', 100), PitchPercent = -900, Speed = 200, Pause = 0, VariancePercent = 400,
        };
        var edited = profile.WithSpeechBarks(voice);
        Assert.Multiple(() =>
        {
            Assert.That(edited.SpeechBarks.Voice, Is.Empty);
            Assert.That(edited.SpeechBarks.PitchPercent, Is.EqualTo(1));
            Assert.That(edited.SpeechBarks.Speed, Is.EqualTo(100));
            Assert.That(edited.SpeechBarks.Pause, Is.EqualTo(SpeechBarkSettings.MinPause));
            Assert.That(edited.SpeechBarks.VariancePercent, Is.EqualTo(100));
            Assert.That(edited.WithName("Test").SpeechBarks, Is.EqualTo(edited.SpeechBarks));
            Assert.That(profile.MemberwiseEquals(edited), Is.False);
            Assert.That(profile.SpeechBarks, Is.EqualTo(new SpeechBarkSettings()));
        });
    }

    [Test]
    public async Task SqliteMigrationAndProfileRoundTrip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            await context.GetService<IMigrator>().MigrateAsync("20261105120000_CustomSpeciesName");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color) VALUES ({user.UserId}, 0, '#FFFFFF')");
            await context.Database.MigrateAsync();
            Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        }

        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
        var voice = new SpeechBarkSettings
        {
            Voice = "bottalk2", PitchPercent = 75, Speed = 3, Pause = 142, VariancePercent = 37,
        };
        await database.SaveCharacterSlotAsync(user, profile.WithSpeechBarks(voice), 0);
        await database.SaveCharacterSlotAsync(user, profile, 1);
        var saved = await database.GetPlayerPreferencesAsync(user);
        Assert.That(((HumanoidCharacterProfile) saved!.Characters[0]).SpeechBarks, Is.EqualTo(voice));
        Assert.That(((HumanoidCharacterProfile) saved.Characters[1]).SpeechBarks, Is.EqualTo(new SpeechBarkSettings()));

        const string oldJson = """{"Timbre":6,"Pitch":-5,"Tempo":118,"Pause":142,"Intonation":185}""";
        await using (var context = new SqliteServerDbContext(options))
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE profile SET speech_bark_settings_json = {oldJson} WHERE slot = 1");
        var upgraded = await database.GetPlayerPreferencesAsync(user);
        Assert.That(((HumanoidCharacterProfile) upgraded!.Characters[1]).SpeechBarks, Is.EqualTo(voice));
    }

    [Test]
    public void PostgresMigrationAddsDefaultedVoiceSettings()
    {
        using var context = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var sql = context.GetService<IMigrator>().GenerateScript("20261105120000_CustomSpeciesName", "20261108120000_SpeechBarkSettings");
        Assert.That(sql, Does.Contain("ADD speech_bark_settings_json text NOT NULL DEFAULT '{}'"));
    }
}
