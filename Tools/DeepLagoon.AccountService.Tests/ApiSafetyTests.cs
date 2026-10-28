using System.Net;
using Content.Server.Database;
using Content.Server._DeepLagoon.DiscordLink;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    [Test]
    public async Task ClaimedHostDoesNotReplaceLinkedActiveGameAuthority()
    {
        await using var db = _settings.OpenDatabase();
        var reviewer = await db.Admin.SingleAsync();
        reviewer.Suspended = true;
        await db.SaveChangesAsync();
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        reviewer.Suspended = false; reviewer.Deadminned = true;
        await db.SaveChangesAsync();
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        reviewer.Deadminned = false;
        await db.SaveChangesAsync();
        Links("DELETE FROM discord_links WHERE discord_id=$id", ("$id", Actor));
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await db.LagoonCoinOperations.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RoleRevocationStopsPreviouslyAcceptedReceiptAndNewWrites()
    {
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.OK));
        await using var db = _settings.OpenDatabase();
        await db.Admin.ExecuteUpdateAsync(s => s.SetProperty(a => a.Suspended, true));
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Call("lc_add", Change(receipt: "1554164947804487890"))).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await db.LagoonCoinOperations.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task IndependentSupporterCapabilityCannotBeReplacedByCoinKey()
    {
        var observation = new { discord_id = Target, actor_discord_id = Target,
            request_id = Receipt, external_tier = 4, observed_at = DateTime.UtcNow };
        Assert.That((await Call("economy_supporter", observation, _settings.CoinToken)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Call("economy_supporter", observation, _settings.SupporterToken)).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("lc_add", Change(), _settings.SupporterToken)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task NewReceiptIdsCannotBypassDurableGlobalGrantBudget()
    {
        Assert.That((await Call("lc_add", Change(6001))).Status, Is.EqualTo(HttpStatusCode.TooManyRequests));
        // The rejected size was bound to that receipt: use fresh, synthetic IDs.
        var first = Change(6000, "1554164947804487891");
        Assert.That((await Call("lc_add", first)).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("lc_add", first)).Body.GetProperty("applied").GetBoolean(), Is.False);
        Assert.That((await Call("lc_add", Change(6000, "1554164947804487892"))).Status, Is.EqualTo(HttpStatusCode.OK));
        using var restarted = new AccountOperations(_settings);
        var error = Assert.ThrowsAsync<AccountError>(async () => await restarted.Execute("lc_add",
            new AccountRequest(Target, Actor, "1554164947804487893", 1, "new id", true)));
        Assert.That(error!.Code, Is.EqualTo("grant_limit"));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Preference.Where(p => p.UserId == _user).Select(p => p.LagoonCoins).SingleAsync(), Is.EqualTo(12000));
    }

    [Test]
    public async Task WhitelistCapabilityCannotMakeUnboundedChanges()
    {
        for (var index = 0; index < 31; index++)
        {
            var discord = (1554164947804488000L + index).ToString();
            Links("INSERT INTO discord_links VALUES($id,$uid,'Player')", ("$id", discord), ("$uid", Guid.NewGuid().ToString()));
            Assert.That((await Call("whitelist", new { discord_id = discord })).Status,
                Is.EqualTo(index < 30 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests));
        }
        // A no-op retry is allowed even when the mutation budget is exhausted.
        Assert.That((await Call("whitelist", new { discord_id = "1554164947804488000" })).Status, Is.EqualTo(HttpStatusCode.OK));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Whitelist.CountAsync(), Is.EqualTo(30));
    }
}

[TestFixture]
public sealed class NativeApiSafetyTests
{
    [Test]
    public void RecoveryRejectsMissingRootAndBotCapabilities()
    {
        var root = new string('r', 64);
        var bot = new string('b', 64);
        Assert.That(DiscordApiTokens.Recovery("", root, [bot]), Is.Empty);
        Assert.That(DiscordApiTokens.Recovery(root, root, [bot]), Is.Empty);
        Assert.That(DiscordApiTokens.Recovery(bot, root, [bot]), Is.Empty);
        foreach (var label in new[] { "lagoon-api-identity-v1", "lagoon-api-moderation-v1", "lagoon-launcher-enrollment-v1" })
        {
            var derived = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(root), System.Text.Encoding.UTF8.GetBytes(label))).ToLowerInvariant();
            Assert.That(DiscordApiTokens.Recovery(derived, root, [bot]), Is.Empty);
        }
        Assert.That(DiscordApiTokens.Recovery(new string('c', 64), root, [bot]), Is.EqualTo(new string('c', 64)));
    }

    [Test]
    public void BanCooldownSurvivesReconnectAndCannotBeReservedTwice()
    {
        SQLitePCL.Batteries_V2.Init();
        var folder = Path.Combine(Path.GetTempPath(), "native-api-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = 1000L;
            var path = Path.Combine(folder, "links.db");
            using (var first = new DiscordLinkStore(path, () => now))
            using (var second = new DiscordLinkStore(path, () => now))
            {
                Assert.That(first.ReserveApiCooldown("ban", 600), Is.True);
                Assert.That(second.ReserveApiCooldown("ban", 600), Is.False);
                Assert.That(second.ReserveApiCooldown("moderation-write", 5), Is.True);
            }
            using (var reopened = new DiscordLinkStore(path, () => now))
            {
                now = 1599; Assert.That(reopened.ReserveApiCooldown("ban", 600), Is.False);
                now = 1600; Assert.That(reopened.ReserveApiCooldown("ban", 600), Is.True);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Test]
    public void DatabasePermissionsHonorSuspensionDeadminAndNegativeFlags()
    {
        var admin = new Admin { Flags = [new AdminFlag { Flag = "BAN" }] };
        Assert.That(DiscordApiAuthority.Has(null, "BAN"), Is.False);
        Assert.That(DiscordApiAuthority.Has(admin, "BAN"), Is.True);
        Assert.That(DiscordApiAuthority.Has(admin, "PERMISSIONS"), Is.False);
        admin.Suspended = true; Assert.That(DiscordApiAuthority.Has(admin, "BAN"), Is.False);
        admin.Suspended = false; admin.Deadminned = true;
        Assert.That(DiscordApiAuthority.Has(admin, "BAN"), Is.False);
        admin.Deadminned = false; admin.Flags.Add(new AdminFlag { Flag = "BAN", Negative = true });
        Assert.That(DiscordApiAuthority.Has(admin, "BAN"), Is.False);
    }
}
