using System.Net;
using Content.Server.Database;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    [Test]
    public async Task WhitelistReviewBlocksActiveHardwareMatchAndIgnoresLiftedBans()
    {
        await using var db = _settings.OpenDatabase();
        var ban = await RecordRelatedBan(db, sharedAddress: false, matchingHardwareType: true);
        var review = await Call("whitelist_check", new { discord_id = Target });
        Assert.That(review.Status, Is.EqualTo(HttpStatusCode.OK), review.Body.ToString());
        var result = review.Body.GetProperty("review");
        Assert.That(result.GetProperty("hwid_match").GetBoolean(), Is.True);
        Assert.That(result.GetProperty("active_ban").GetBoolean(), Is.True);
        Assert.That(result.GetProperty("ip_match").GetBoolean(), Is.False);
        Assert.That(result.ToString(), Does.Not.Contain("192.0.2.").And.Not.Contain("Private ban reason"));
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);

        var blocked = await Call("whitelist", new { discord_id = Target });
        Assert.That(blocked.Status, Is.EqualTo(HttpStatusCode.Conflict), blocked.Body.ToString());
        Assert.That(blocked.Body.GetProperty("error").GetString(), Is.EqualTo("whitelist_active_ban"));
        Assert.That((await Call("whitelist", new
            { discord_id = Target, accept_shared_ip = true, host_authorized = true, actor_discord_id = Actor })).Status,
            Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);

        db.Unban.Add(new ServerUnban { Ban = ban, UnbanTime = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Ban.Add(new ServerBan { PlayerUserId = _other, Reason = "Expired", BanTime = DateTime.UtcNow.AddDays(-2),
            ExpirationTime = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var added = await Call("whitelist", new { discord_id = Target });
        Assert.That(added.Status, Is.EqualTo(HttpStatusCode.OK), added.Body.ToString());
        Assert.That(added.Body.GetProperty("review").GetProperty("hwid_match").GetBoolean(), Is.True);
    }

    [Test]
    public async Task WhitelistReviewRequiresActualGameHostForAddressOnlyMatch()
    {
        await using var db = _settings.OpenDatabase();
        await RecordRelatedBan(db, sharedAddress: true, matchingHardwareType: false);
        db.Whitelist.Add(new Whitelist { UserId = _other });
        await db.SaveChangesAsync();
        var blocked = await Call("whitelist", new { discord_id = Target });
        Assert.That(blocked.Status, Is.EqualTo(HttpStatusCode.Conflict), blocked.Body.ToString());
        Assert.That(blocked.Body.GetProperty("error").GetString(), Is.EqualTo("whitelist_shared_ip_review"));
        var review = blocked.Body.GetProperty("review");
        Assert.That(review.GetProperty("hwid_match").GetBoolean(), Is.False);
        Assert.That(review.GetProperty("ip_match").GetBoolean(), Is.True);
        Assert.That(review.GetProperty("related_whitelist").GetBoolean(), Is.True);
        Assert.That((await Call("whitelist", new
            { discord_id = Target, accept_shared_ip = true, host_authorized = true, actor_discord_id = Target })).Status,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);
        Assert.That((await Call("whitelist", new
            { discord_id = Target, accept_shared_ip = true, host_authorized = true, actor_discord_id = Actor })).Status,
            Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WhitelistReviewRequiresConnectionAndChecksBothSourcesOfDiscordHistory()
    {
        await using var db = _settings.OpenDatabase();
        await db.ConnectionLog.Where(c => c.UserId == _user).ExecuteDeleteAsync();
        var missing = await Call("whitelist", new { discord_id = Target });
        Assert.That(missing.Status, Is.EqualTo(HttpStatusCode.Conflict), missing.Body.ToString());
        Assert.That(missing.Body.GetProperty("error").GetString(), Is.EqualTo("whitelist_connection_required"));

        db.Ban.Add(new ServerBan { PlayerUserId = _other, Reason = "Private ban reason", BanTime = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var fromTicket = await Call("whitelist", new { discord_id = Target, known_uids = new[] { _other } });
        Assert.That(fromTicket.Body.GetProperty("error").GetString(), Is.EqualTo("whitelist_active_ban"));
        Links("INSERT INTO discord_link_history VALUES($id,$uid)", ("$id", Target), ("$uid", _other.ToString()));
        var fromHistory = await Call("whitelist", new { discord_id = Target });
        Assert.That(fromHistory.Body.GetProperty("error").GetString(), Is.EqualTo("whitelist_active_ban"));
        Assert.That(fromHistory.Body.GetProperty("review").GetProperty("discord_history").GetBoolean(), Is.True);
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);
    }

    private async Task<ServerBan> RecordRelatedBan(ServerDbContext db, bool sharedAddress, bool matchingHardwareType)
    {
        var current = await db.ConnectionLog.SingleAsync(c => c.UserId == _user);
        current.HWId = new TypedHwid { Hwid = [12, 15, 42], Type = HwidType.Modern };
        db.ConnectionLog.Add(new ConnectionLog
        {
            UserId = _other, UserName = "Other", Address = sharedAddress ? current.Address : IPAddress.Parse("192.0.2.2"),
            Time = current.Time.AddDays(-1), ServerId = current.ServerId, Denied = ConnectionDenyReason.Ban,
            HWId = new TypedHwid { Hwid = [12, 15, 42], Type = matchingHardwareType ? HwidType.Modern : HwidType.Legacy },
        });
        var ban = new ServerBan { PlayerUserId = _other, Reason = "Private ban reason", BanTime = current.Time.AddDays(-2) };
        db.Ban.Add(ban);
        await db.SaveChangesAsync();
        return ban;
    }
}
