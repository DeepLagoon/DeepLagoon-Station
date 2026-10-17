using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Audio.Jukebox;
using Content.Server.Database;
using Content.Shared.Audio.Jukebox;
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
public sealed class JukeboxTests
{
    [Test]
    public void RepeatQueueRetainsFullQueueAndDistinctDuplicateEntries()
    {
        var queue = Enumerable.Range(0, JukeboxRules.MaxQueue).Select(i => new JukeboxQueueEntry(i.ToString(), "same")).ToList();
        Assert.That(JukeboxRules.Advance(queue, "current", JukeboxRepeat.Queue, false), Is.EqualTo("same"));
        Assert.That(queue.Count, Is.EqualTo(JukeboxRules.MaxQueue));
        Assert.That(queue[^1].Track, Is.EqualTo("current"));
        Assert.That(queue.Select(e => e.Id).Distinct().Count(), Is.EqualTo(queue.Count));
        Assert.That(JukeboxRules.Move(queue, 1, 0), Is.True);
        Assert.That(queue[0].Id, Is.EqualTo("2"));
    }

    [Test]
    public void TrackRepeatDoesNotConsumeQueueAndSkipOverridesIt()
    {
        var queue = new List<JukeboxQueueEntry> { new("a", "next") };
        Assert.That(JukeboxRules.Advance(queue, "current", JukeboxRepeat.Track, false), Is.EqualTo("current"));
        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(JukeboxRules.Advance(queue, "current", JukeboxRepeat.Track, true), Is.EqualTo("next"));
        Assert.That(queue, Is.Empty);
        Assert.That(JukeboxRules.Advance(queue, "current", JukeboxRepeat.Off, false), Is.Null);
        Assert.That(JukeboxRules.Advance(queue, "current", JukeboxRepeat.Queue, false), Is.EqualTo("current"));
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{\"Favorites\":null}")]
    [TestCase("{\"Playlists\":{\"x\":null}}")]
    [TestCase("{\"Favorites\":[\"same\",\"same\"]}")]
    [TestCase("{\"Favorites\":[1]}")]
    [TestCase("{\"Playlists\":{\"bad\\nname\":[]}}")]
    [TestCase("{\"Favorites\":[],\"Favorites\":[\"hidden\"]}")]
    [TestCase("{\"Extra\":1}")]
    [TestCase("{\"Playlists\":{\"x\":[],\"x\":[\"overwritten\"]}}")]
    public void InvalidImportCannotReplaceLibrary(string json) => Assert.That(JukeboxSystem.ReadLibrary(json), Is.Null);

    [Test]
    public void LibrarySupportsUnicodeAndUnavailableIdsButEnforcesLimits()
    {
        var library = JukeboxSystem.ReadLibrary("{\"Favorites\":[\"removed-track\"],\"Playlists\":{\"Вечер в баре\":[\"track\"]}}");
        Assert.That(library, Is.Not.Null);
        var copy = library.Copy(); copy.Favorites.Add("other"); copy.Playlists["Вечер в баре"].Clear();
        Assert.That(library.Favorites.Count, Is.EqualTo(1));
        Assert.That(library.Playlists["Вечер в баре"].Count, Is.EqualTo(1));
        copy.Favorites = Enumerable.Range(0, 1025).Select(i => i.ToString()).ToList();
        Assert.That(JukeboxRules.ValidLibrary(copy), Is.False);
        Assert.That(JukeboxSystem.ReadLibrary(new string(' ', JukeboxRules.MaxJson + 1)), Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void MigrationMatchesModelAndIsAdditive(bool postgres)
    {
        using DbContext context = postgres
            ? new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused").Options)
            : new SqliteServerDbContext(new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        var previous = context.Database.GetMigrations().TakeWhile(id => id != "20261017120000_JukeboxLibrary").Last();
        var sql = context.GetService<IMigrator>().GenerateScript(previous, "20261017120000_JukeboxLibrary");
        Assert.That(sql, Does.Contain("jukebox_library_json"));
        Assert.That(sql, Does.Not.Contain("DROP TABLE"));
    }

    [Test]
    public async Task LibraryPersistsPerAccountWithoutOverwritingCharacterOrChat()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var user = new NetUserId(Guid.NewGuid()); var other = new NetUserId(Guid.NewGuid());
        await using (var context = new SqliteServerDbContext(options))
        {
            var previous = context.Database.GetMigrations().TakeWhile(id => id != "20261017120000_JukeboxLibrary").Last();
            await context.GetService<IMigrator>().MigrateAsync(previous);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color, mono_coins, chat_panel_settings, social_actions_json, lagoon_coins, lagoon_coin_played_ticks, lagoon_coin_bonus_ticks) VALUES ({user.UserId}, 0, '#FF0000', 42, 'chat', '[]', 0, 0, 0)");
            await context.Database.MigrateAsync();
            Assert.That((await context.Preference.SingleAsync()).JukeboxLibraryJson, Is.EqualTo("{}"));
            context.Preference.Add(new Preference { UserId = other.UserId, AdminOOCColor = "#00FF00" });
            await context.SaveChangesAsync();
        }
        var db = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        const string json = "{\"Favorites\":[\"track\"],\"Playlists\":{\"Вечер\":[\"track\"]}}";
        Assert.That(await db.SaveJukeboxLibraryAsync(user, json), Is.True);
        Assert.That(await db.GetJukeboxLibraryAsync(user), Is.EqualTo(json));
        Assert.That(await db.GetJukeboxLibraryAsync(other), Is.EqualTo("{}"));
        Assert.That(await db.SaveJukeboxLibraryAsync(new NetUserId(Guid.NewGuid()), json), Is.False);
        await db.SaveCharacterSlotAsync(user, new Content.Shared.Preferences.HumanoidCharacterProfile(), 0);
        await using var read = new SqliteServerDbContext(options);
        var saved = await read.Preference.SingleAsync(p => p.UserId == user.UserId);
        Assert.Multiple(() =>
        {
            Assert.That(saved.JukeboxLibraryJson, Is.EqualTo(json));
            Assert.That(saved.ChatPanelSettings, Is.EqualTo("chat"));
            Assert.That(saved.Money, Is.EqualTo(42));
        });
    }
}
