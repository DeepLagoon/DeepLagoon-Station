using System;
using System.Net;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.DiscordLink;
using Content.Server.Database;
using Content.Shared.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Tests.Server;

[TestFixture]
public sealed class PermanentBanDatabaseTest
{
    [Test]
    public async Task OnlyUnpardonedPermanentAccountBansAreListed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var player = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await database.UpdatePlayerRecord(player, "Player", IPAddress.Loopback, null);
        await database.UpdatePlayerRecord(other, "Other", IPAddress.Loopback, null);

        ServerBanDef Ban(NetUserId? user, DateTimeOffset? expires = null) => new(null, user,
            (IPAddress.Loopback, 32), null, now, expires, null, TimeSpan.Zero, "test", NoteSeverity.High, null, null);

        var first = await database.AddServerBanAsync(Ban(player));
        var second = await database.AddServerBanAsync(Ban(player));
        await database.AddServerBanAsync(Ban(other, now.AddHours(1)));
        await database.AddServerBanAsync(Ban(null));
        await database.AddServerRoleBanAsync(new ServerRoleBanDef(null, other, null, null, now,
            null, null, TimeSpan.Zero, "test", NoteSeverity.High, null, null, "Job:Captain"));

        Assert.That(await database.GetActivePermanentBanUsersAsync(), Is.EqualTo(new[] { player.UserId }));
        await database.AddServerUnbanAsync(new ServerUnbanDef(first, null, now));
        Assert.That(await database.GetActivePermanentBanUsersAsync(), Is.EqualTo(new[] { player.UserId }));
        await database.AddServerUnbanAsync(new ServerUnbanDef(second, null, now));
        Assert.That(await database.GetActivePermanentBanUsersAsync(), Is.Empty);
    }

    [Test]
    public void ReverseLinkLookupUsesTheCurrentDiscordAccount()
    {
        const string discordId = "123456789012345678";
        using var store = new DiscordLinkStore(":memory:");
        var canonical = store.EnrollLauncher(discordId).Uid;
        var official = Guid.NewGuid();
        var code = store.IssueMerge(official, "Official");
        var plan = store.PrepareMerge(discordId, code);
        store.CompleteMerge(discordId, code, plan);

        Assert.That(store.FindDiscordId(canonical), Is.EqualTo(discordId));
        Assert.That(store.FindDiscordId(official), Is.EqualTo(discordId));
        store.Unlink(discordId, canonical);
        Assert.That(store.FindDiscordId(official), Is.Null);
    }
}
