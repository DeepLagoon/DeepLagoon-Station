using System;
using System.IO;
using Content.Server._DeepLagoon.DiscordLink;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
[NonParallelizable]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class DiscordLinkStoreTests
{
    private string _directory = default!;
    private string _path = default!;
    private long _now;

    [SetUp]
    public void Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _directory = Path.Combine(Path.GetTempPath(), "ss14-discord-link-" + Guid.NewGuid());
        _path = Path.Combine(_directory, "discord-links.db");
        _now = 10000;
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Test]
    public void CodeIsSingleUseAndMappingSurvivesRestart()
    {
        var uid = Guid.NewGuid();
        string code;
        using (var store = new DiscordLinkStore(_path, () => _now))
        {
            code = store.Issue(uid, "Player");
            Assert.That(code, Has.Length.EqualTo(24));
            Assert.That(store.Consume("1554565156657299597", code).Uid, Is.EqualTo(uid));
            Assert.That(store.IsLinked(uid), Is.True);
            Assert.That(() => store.Consume("1554565156657299598", code), Throws.TypeOf<DiscordLinkStore.LinkException>());
            Assert.That(() => store.Issue(uid, "Player"), Throws.TypeOf<DiscordLinkStore.LinkException>());
        }
        using var reopened = new DiscordLinkStore(_path, () => _now);
        Assert.That(reopened.FindDiscord("1554565156657299597")!.Uid, Is.EqualTo(uid));
        Assert.That(() => reopened.Consume("1554565156657299597", code), Throws.TypeOf<DiscordLinkStore.LinkException>());
    }

    [Test]
    public void ExpiryRegenerationAndHashStorage()
    {
        using var store = new DiscordLinkStore(_path, () => _now);
        var uid = Guid.NewGuid();
        var old = store.Issue(uid, "Player");
        Assert.That(() => store.Issue(uid, "Player"), Throws.TypeOf<DiscordLinkStore.LinkException>());
        _now += 31;
        var current = store.Issue(uid, "Player");
        Assert.That(() => store.Consume("1554565156657299597", old), Throws.TypeOf<DiscordLinkStore.LinkException>());
        using (var db = new SqliteConnection("Data Source=" + _path))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT code_hash FROM discord_link_codes";
            Assert.That(command.ExecuteScalar(), Is.Not.EqualTo(current));
        }
        _now += 600;
        Assert.That(() => store.Consume("1554565156657299597", current), Throws.TypeOf<DiscordLinkStore.LinkException>());
        Assert.That(store.IsLinked(uid), Is.False);
    }

    [Test]
    public void AttemptsPersistAndExistingDiscordCannotBeOverwritten()
    {
        var uid = Guid.NewGuid();
        string code;
        using (var store = new DiscordLinkStore(_path, () => _now))
        {
            code = store.Issue(uid, "Player");
            for (var i = 0; i < 10; i++)
                Assert.That(() => store.Consume("1554565156657299597", "wrong"), Throws.TypeOf<DiscordLinkStore.LinkException>());
        }
        using var reopened = new DiscordLinkStore(_path, () => _now);
        var error = Assert.Throws<DiscordLinkStore.LinkException>(() => reopened.Consume("1554565156657299597", code));
        Assert.That(error!.Message, Is.EqualTo("rate_limited"));
        _now += 601;
        var fresh = reopened.Issue(uid, "Player");
        reopened.Consume("1554565156657299597", fresh);
        var otherUid = Guid.NewGuid();
        var otherCode = reopened.Issue(otherUid, "Other");
        Assert.That(() => reopened.Consume("1554565156657299597", otherCode), Throws.TypeOf<DiscordLinkStore.LinkException>());
        Assert.That(reopened.FindDiscord("1554565156657299597")!.Uid, Is.EqualTo(uid));
        Assert.That(reopened.Consume("1554565156657299598", otherCode).Uid, Is.EqualTo(otherUid));
    }
    [Test]
    public void UnlinkRequiresMatchingIdentityAndAllowsFreshLink()
    {
        var uid = Guid.NewGuid();
        using var store = new DiscordLinkStore(_path, () => _now);
        var code = store.Issue(uid, "Player");
        store.Consume("1554565156657299597", code);
        Assert.That(() => store.Unlink("1554565156657299597", Guid.NewGuid()), Throws.TypeOf<DiscordLinkStore.LinkException>());
        Assert.That(store.IsLinked(uid), Is.True);
        store.Unlink("1554565156657299597", uid);
        Assert.That(store.IsLinked(uid), Is.False);
        Assert.That(store.FindDiscord("1554565156657299597"), Is.Null);
        Assert.That(() => store.Consume("1554565156657299597", code), Throws.TypeOf<DiscordLinkStore.LinkException>());
        store.Consume("1554565156657299598", store.Issue(uid, "Player"));
        Assert.That(store.FindDiscord("1554565156657299598")!.Uid, Is.EqualTo(uid));
    }
}
