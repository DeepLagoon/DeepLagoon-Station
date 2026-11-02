using System.Net;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    [Test]
    public async Task ConfirmedPaidPeriodSurvivesRoleRemovalAndExpiresWithoutDoubleGift()
    {
        LinkReviewer();
        var now = DateTime.UtcNow;
        var paidAt = now.AddDays(-1);
        var until = now.AddDays(1);
        object Payment(string request, DateTime expiry) => new {
            discord_id = Target, actor_discord_id = Actor, request_id = request,
            host_authorized = true, receipt = "boosty:paid-hidden", tier = 4,
            paid_at = paidAt, paid_until = expiry, historical = false };
        var paid = await Call("economy_payment", Payment(Receipt, until));
        Assert.That(paid.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(paid.Body.GetProperty("awarded").GetInt64(), Is.EqualTo(1000));
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.SyncExternal(db, _user, 0, now.AddSeconds(1), now.AddSeconds(1));
        var hidden = await Economy(_user, at: now.AddSeconds(2));
        Assert.That(hidden.State.ExternalTier, Is.EqualTo(4));
        Assert.That(hidden.State.ExternalExpiresAt, Is.EqualTo(until));
        Assert.That(hidden.State.PassTier, Is.Zero, "Paid Boosty access must not fabricate an LC purchase");
        var replay = await Call("economy_payment", Payment("1554164947804487991", until));
        Assert.That(replay.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(replay.Body.GetProperty("applied").GetBoolean(), Is.False);
        Assert.That((await Call("economy_payment", Payment("1554164947804487992", until.AddDays(1)))).Status,
            Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Economy(_user, at: until.AddTicks(1))).State.ExternalTier, Is.Zero);
        await using var verify = _settings.OpenDatabase();
        Assert.That(await verify.LagoonCoinOperations.CountAsync(p => p.UserId == _user && p.OperationId == "payment:boosty:paid-hidden"), Is.EqualTo(1));
        Assert.That(await verify.Preference.Where(p => p.UserId == _user).Select(p => p.Money).SingleAsync(), Is.EqualTo(777));
    }

    [Test]
    public async Task PaidPeriodUsesCalendarMonthAndHistoricalRecordsNeverGrantAccess()
    {
        var now = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.ConfirmPayment(db, _user, _other, "calendar", 4, "Test", now, false, now);
        var state = await Economy(_user, at: now.AddDays(1));
        Assert.That(state.State.ExternalTier, Is.EqualTo(4));
        Assert.That(state.State.ExternalExpiresAt, Is.EqualTo(now.AddMonths(1)));
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.ConfirmPayment(db, _other, _user, "historical-only", 4, "Other", now, true, now);
        var historical = await Economy(_other, at: now.AddDays(1));
        Assert.That(historical.Balance, Is.Zero);
        Assert.That(historical.State.ExternalTier, Is.Zero);
    }

    [Test]
    public async Task PaidPeriodDoesNotExtendHigherDiscordTierAndFallsBackAfterExpiry()
    {
        var now = DateTime.UtcNow;
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.SyncExternal(db, _user, 4, now, now);
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.ConfirmPayment(db, _user, _other, "lower-paid", 2, "Test", now, false, now.AddSeconds(1));
        var high = await Economy(_user, at: now.AddSeconds(2));
        Assert.That(high.State.ExternalTier, Is.EqualTo(4));
        Assert.That(high.State.ExternalExpiresAt, Is.EqualTo(now.AddMinutes(15)));
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.SyncExternal(db, _user, 0, now.AddMinutes(16), now.AddMinutes(16));
        Assert.That((await Economy(_user, at: now.AddMinutes(16))).State.ExternalTier, Is.EqualTo(2));
    }

    [Test]
    public async Task HiddenPaidPeriodPausesExistingLCPassAndRollsBackOnIdentityFailure()
    {
        await Fund(1000);
        var now = DateTime.UtcNow;
        await Buy("lc-before-paid", "pass", "2", at: now);
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.ConfirmPayment(db, _user, _other, "paused", 4, "Test", now, false, now, paidUntil: now.AddDays(1));
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.SyncExternal(db, _user, 0, now.AddHours(12), now.AddHours(12));
        Assert.That((await Economy(_user, at: now.AddHours(12))).State.PassTicks, Is.EqualTo(TimeSpan.FromDays(30).Ticks));
        Assert.That((await Economy(_user, at: now.AddDays(2))).State.PassTicks, Is.EqualTo(TimeSpan.FromDays(29).Ticks));
        await using (var db = _settings.OpenDatabase())
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await AccountEconomyOperations.ConfirmPayment(db, _other, _user, "rollback", 4, "Other", now, false, now,
                    beforeCommit: () => throw new InvalidOperationException("Changed identity")));
        Assert.That((await Economy(_other, at: now)).Balance, Is.Zero);
        await using var verify = _settings.OpenDatabase();
        Assert.That(await verify.LagoonEconomyRecords.AnyAsync(p => p.Id == "payment:rollback"), Is.False);
    }
}
