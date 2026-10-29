using System.Net;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    private const string BugChannel = "1555662231738519706";

    private static object BugReward(long amount = 10, string receipt = Receipt, string channel = BugChannel,
        string category = "bug:code", string target = Target, string actor = Actor, bool host = false) =>
        new { discord_id = target, actor_discord_id = actor, request_id = receipt, work_channel = channel,
            category, amount, host_authorized = host };

    [Test]
    public async Task BugRewardAllowsNonHostConfirmerWithoutGrantingManualCoinAuthority()
    {
        Links("UPDATE discord_links SET ss14_uid=$uid WHERE discord_id=$id", ("$uid", _other.ToString()), ("$id", Actor));
        var result = await Call("bug_report_reward", BugReward());
        Assert.That(result.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(result.Body.GetProperty("applied").GetBoolean(), Is.True);
        Assert.That(result.Body.GetProperty("balance").GetInt64(), Is.EqualTo(10));
        Assert.That((await Call("lc_add", Change(receipt: "1554164947804487799"))).Status, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task BugRewardRetriesWithNewInteractionIdsStillPayOncePerPublication()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Call("bug_report_reward", BugReward(receipt: (1554164947804487800L + i).ToString()))));
        Assert.That(results.All(r => r.Status == HttpStatusCode.OK), Is.True);
        Assert.That(results.Count(r => r.Body.GetProperty("applied").GetBoolean()), Is.EqualTo(1));
        await using var db = _settings.OpenDatabase();
        Assert.That((await db.Preference.SingleAsync(p => p.UserId == _user)).LagoonCoins, Is.EqualTo(10));
        Assert.That(await db.LagoonCoinOperations.CountAsync(o => o.UserId == _user), Is.EqualTo(1));
    }

    [Test]
    public async Task BugRewardRejectsWrongAmountCategoryChannelAndCrossScopeToken()
    {
        foreach (var payload in new[] { BugReward(0), BugReward(9), BugReward(11),
                     BugReward(category: "code:fixes"), BugReward(channel: "invalid") })
            Assert.That((await Call("bug_report_reward", payload)).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await Call("bug_report_reward", BugReward(), _settings.WhitelistToken)).Status,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Call("bug_report_reward", BugReward())).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("bug_report_reward", BugReward(category: "bug:map"))).Body.GetProperty("error").GetString(),
            Is.EqualTo("operation_conflict"));
    }

    [Test]
    public async Task BugRewardCannotBeSelfConfirmedByNonHostOrReplayedAfterRelinking()
    {
        Assert.That((await Call("bug_report_reward", BugReward(actor: Target))).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Call("bug_report_reward", BugReward(actor: Target, host: true))).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Call("bug_report_reward", BugReward())).Status, Is.EqualTo(HttpStatusCode.OK));
        Links("UPDATE discord_links SET ss14_uid=$uid WHERE discord_id=$id", ("$uid", _other.ToString()), ("$id", Target));
        Assert.That((await Call("bug_report_reward", BugReward())).Body.GetProperty("error").GetString(),
            Is.EqualTo("operation_conflict"));
    }
}
