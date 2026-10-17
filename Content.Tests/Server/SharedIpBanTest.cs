using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
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
public sealed class SharedIpBanTest
{
    [TestCase(false)]
    [TestCase(true)]
    public void PostgresQueriesTranslatePersonalBanIpGuard(bool roleBan)
    {
        using var context = new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        // Exercise the actual lookup builder without requiring a running PostgreSQL server.
        var backend = typeof(ServerDbPostgres);
        var guardType = backend.GetNestedType("DbGuardImpl", BindingFlags.NonPublic)!;
        var guard = Activator.CreateInstance(guardType, new object[] { null, context });
        var address = IPAddress.Parse("192.0.2.10");
        var user = new NetUserId(Guid.NewGuid());
        var method = backend.GetMethod(roleBan ? "MakeRoleBanLookupQuery" : "MakeBanLookupQuery",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = roleBan
            ? new object[] { address, user, null, null, guard, false }
            : new object[] { address, user, null, null, guard, false, ServerBanExemptFlags.None, true };
        var query = (IQueryable) method.Invoke(null, args)!;
        var sql = query.ToQueryString();
        Assert.That(sql, Does.Contain("player_user_id IS NULL"));
        Assert.That(sql, Does.Contain("hwid IS NULL"));
    }

    [TestCase("192.0.2.10", "192.0.2.10", 32, HwidType.Legacy)]
    [TestCase("2001:db8::1", "2001:db8::2", 64, HwidType.Modern)]
    public void PersonalBanDoesNotMatchSharedAddress(string bannedIp, string sharedIp, int prefix, HwidType type)
    {
        var owner = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        var hwid = new TypedHwid { Type = type, Hwid = new byte[] { 1, 2, 3 } };
        var address = (IPAddress.Parse(bannedIp), prefix);
        var player = new BanMatcher.PlayerInfo { UserId = other, Address = IPAddress.Parse(sharedIp) };

        // Old records containing both personal identifiers and IPs must also be safe.
        Assert.That(BanMatcher.BanMatches(Ban(owner, address, hwid), player), Is.False);
        Assert.That(BanMatcher.BanMatches(Ban(owner, address, null), player), Is.False);
        Assert.That(BanMatcher.BanMatches(Ban(null, address, hwid), player), Is.False);

        player.UserId = owner;
        player.Address = IPAddress.Parse("198.51.100.20");
        Assert.That(BanMatcher.BanMatches(Ban(owner, address, hwid), player), Is.True);

        player.UserId = other;
        if (type == HwidType.Legacy)
            player.HWId = hwid.Hwid.ToImmutableArray();
        else
            player.ModernHWIds = ImmutableArray.Create(hwid.Hwid.ToImmutableArray());
        Assert.That(BanMatcher.BanMatches(Ban(owner, address, hwid), player), Is.True);
    }

    [Test]
    public void ExplicitAddressBanStillHonorsIpExemption()
    {
        var address = IPAddress.Parse("192.0.2.10");
        var ban = Ban(null, (address, 32), null);
        var player = new BanMatcher.PlayerInfo { UserId = new NetUserId(Guid.NewGuid()), Address = address };
        Assert.That(BanMatcher.BanMatches(ban, player), Is.True);
        player.ExemptFlags = ServerBanExemptFlags.IP;
        Assert.That(BanMatcher.BanMatches(ban, player), Is.False);
    }

    [TestCase("192.0.2.10", "192.0.2.10", 32)]
    [TestCase("2001:db8::1", "2001:db8::2", 64)]
    public async Task SqliteServerAndRoleBansDoNotSpreadAcrossSharedAddresses(string bannedIp, string sharedIp, int prefix)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var database = new ServerDbSqlite(() => options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var owner = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        var address = (IPAddress.Parse(bannedIp), prefix);
        var shared = IPAddress.Parse(sharedIp);
        var hwid = new ImmutableTypedHwid(ImmutableArray.Create<byte>(1, 2, 3), HwidType.Modern);
        await database.UpdatePlayerRecord(owner, "Owner", address.Item1, hwid);
        await database.UpdatePlayerRecord(other, "Other", shared, null);
        await database.AddServerBanAsync(Ban(owner, address, hwid));
        await database.AddServerRoleBanAsync(new ServerRoleBanDef(null, owner, address, hwid,
            DateTimeOffset.UtcNow, null, null, TimeSpan.Zero, "test", NoteSeverity.High, null, null, "Job:Captain"));

        Assert.That(await database.GetServerBansAsync(shared, other, null, null, false), Is.Empty);
        Assert.That(await database.GetServerRoleBansAsync(shared, other, null, null, false), Is.Empty);
        var differentIp = IPAddress.Parse("198.51.100.20");
        Assert.That(await database.GetServerBansAsync(differentIp, owner, null, null, false), Has.Count.EqualTo(1));
        Assert.That(await database.GetServerRoleBansAsync(differentIp, owner, null, null, false), Has.Count.EqualTo(1));
        var modernHwids = ImmutableArray.Create(hwid.Hwid);
        Assert.That(await database.GetServerBansAsync(differentIp, other, null, modernHwids, false), Has.Count.EqualTo(1));
        Assert.That(await database.GetServerRoleBansAsync(differentIp, other, null, modernHwids, false), Has.Count.EqualTo(1));

        await database.AddServerBanAsync(Ban(null, address, null));
        Assert.That(await database.GetServerBansAsync(shared, other, null, null, false), Has.Count.EqualTo(1));
        await database.UpdateBanExemption(other, ServerBanExemptFlags.IP);
        Assert.That(await database.GetServerBansAsync(shared, other, null, null, false), Is.Empty);
    }

    private static ServerBanDef Ban(NetUserId? user, (IPAddress, int) address, TypedHwid hwid)
    {
        return new ServerBanDef(null, user, address, hwid, DateTimeOffset.UtcNow, null,
            null, TimeSpan.Zero, "test", NoteSeverity.High, null, null);
    }
}
