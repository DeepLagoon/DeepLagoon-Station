using System.Text.Json;
using Content.Server.Database;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    private async Task Fund(long amount)
    {
        await using var db = _settings.OpenDatabase();
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "test-fund", amount, "test");
    }
    private async Task<LagoonEconomySnapshot> Economy(Guid user, int external = 0, DateTime? at = null)
    {
        await using var db = _settings.OpenDatabase();
        return await AccountEconomyOperations.Get(db, user, external, at ?? DateTime.UtcNow);
    }
    private async Task<LagoonEconomySnapshot> Buy(string key, string kind, string product, long price = 0, int external = 0, DateTime? at = null)
    {
        await using var db = _settings.OpenDatabase();
        return await AccountEconomyOperations.Purchase(db, _user, new(key, kind, product, price, external), at ?? DateTime.UtcNow);
    }
    private async Task<LagoonCoinResult> Play(DateTime day, double minutes, int rate = 2, bool ready = false, string? key = null)
    {
        await using var db = _settings.OpenDatabase();
        return await AccountEconomyOperations.Activity(db, _user, new(key ?? Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(minutes).Ticks, rate, LagoonEconomyRules.MoscowDay(day), "test-round", ready), day);
    }
    [Test]
    public async Task EconomyPurchaseIsAtomicAndReplayDoesNotRenewOrDebitAgain()
    {
        await Fund(1000);
        var now = DateTime.UtcNow;
        var first = await Buy("order-1", "pass", "2", at: now);
        var repeated = await Buy("order-1", "pass", "2", at: now);
        Assert.That(first.Balance, Is.EqualTo(400));
        Assert.That(repeated.Balance, Is.EqualTo(400));
        Assert.That(repeated.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(30).Ticks));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Buy("order-1", "pass", "3"));
        Assert.ThrowsAsync<InsufficientLagoonCoinsException>(async () => await Buy("unaffordable", "pass", "4"));
        var fresh = await Economy(_user, at: now);
        Assert.That(fresh.Balance, Is.EqualTo(400));
        Assert.That(fresh.State.PassTier, Is.EqualTo(2));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Preference.Where(p => p.UserId == _user).Select(p => p.Money).SingleAsync(), Is.EqualTo(777));
    }
    [Test]
    public async Task WeightedTimeDailyReadyAndReplayAreAccountedExactlyOnce()
    {
        var now = DateTime.UtcNow.Date.AddHours(12);
        await Play(now, 20, 5, true, "time-1");
        await Play(now, 20, 5, true, "time-2");
        await Play(now, 20, 5, true, "time-3");
        var repeat = await Play(now, 20, 5, true, "time-3");
        Assert.That(repeat.Applied, Is.False);
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(13));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user && p.Reason == "ready"), Is.EqualTo(1));
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user && p.Reason == "daily-activity"), Is.EqualTo(1));
    }
    [Test]
    public async Task PaidOverlapPausesLCPeriodAndUpgradeKeepsExpiry()
    {
        await Fund(5000);
        var now = DateTime.UtcNow;
        await Buy("pass", "pass", "2", at: now);
        var before = await Economy(_user, 4, now.AddDays(5));
        var paused = await Economy(_user, 4, now.AddDays(10));
        Assert.That(before.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(25).Ticks));
        Assert.That(paused.State.PassTicks, Is.EqualTo(before.State.PassTicks));
        await Economy(_user, 0, now.AddDays(11));
        var resumed = await Economy(_user, 0, now.AddDays(12));
        Assert.That(resumed.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(24).Ticks));
        var upgraded = await Buy("upgrade", "pass", "3", at: now.AddDays(12));
        Assert.That(upgraded.Balance, Is.EqualTo(5000 - 600 - 720));
        Assert.That(upgraded.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(24).Ticks));
    }
    [Test]
    public async Task HistoryKeepsHistoricalPaidSupportersAndExcludesLCPurchases()
    {
        await Fund(1000);
        var now = DateTime.UtcNow;
        await Buy("lc-only", "pass", "1", at: now);
        await using (var db = _settings.OpenDatabase())
        {
            var historical = await AccountEconomyOperations.ConfirmPayment(db, _user, _other, "boosty:old", 4, "Old supporter", now.AddYears(-1), true, now);
            Assert.That(historical.Awarded, Is.Zero);
        }
        await using (var db = _settings.OpenDatabase())
        {
            var paid = await AccountEconomyOperations.ConfirmPayment(db, _other, _user, "boosty:new", 2, "New supporter", now, false, now);
            Assert.That(paid.Awarded, Is.EqualTo(100));
        }
        await using (var db = _settings.OpenDatabase())
        {
            var duplicate = await AccountEconomyOperations.ConfirmPayment(db, _other, _user, "boosty:new", 2, "New supporter", now, false, now);
            Assert.That(duplicate.Applied, Is.False);
            var names = await AccountEconomyOperations.PublicSupporters(db);
            Assert.That(names.Select(p => p.Name), Is.EquivalentTo(new[] { "Old supporter", "New supporter" }));
        }
        await using var changed = _settings.OpenDatabase();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await AccountEconomyOperations.ConfirmPayment(changed, _user, _other, "boosty:new", 2, "Other", now, false, now));
    }
    [Test]
    public async Task TaskCannotBeSelfApprovedAndNewInteractionsCannotPayAgain()
    {
        var now = DateTime.UtcNow;
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.DefineTask(db, _user, _user, "PR-42", "code-small", 250, "Fix accepted bug", now);
        await using (var db = _settings.OpenDatabase()) Assert.ThrowsAsync<InvalidOperationException>(async () => await AccountEconomyOperations.ApproveTask(db, _user, _user, "PR-42", now));
        await using (var db = _settings.OpenDatabase()) Assert.That((await AccountEconomyOperations.ApproveTask(db, _user, _other, "PR-42", now)).Awarded, Is.EqualTo(250));
        await using (var db = _settings.OpenDatabase()) Assert.That((await AccountEconomyOperations.ApproveTask(db, _user, _other, "PR-42", now)).Applied, Is.False);
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(250));
    }
    [Test]
    public async Task ApartmentTemplatesChargeFullPriceRegardlessOfOtherPurchases()
    {
        await Fund(5000);
        var studio = await Buy("studio", "apartment", "Studio", 500);
        Assert.That(studio.Balance, Is.EqualTo(4500));
        var comfort = await Buy("comfort", "apartment", "Comfort", 1200, 4);
        Assert.That(comfort.Balance, Is.EqualTo(3300), "The full price applies even with another apartment and a subscription");
        Assert.That(comfort.Awarded, Is.EqualTo(-1200));
        var cheaper = await Buy("cheaper", "apartment", "Small", 300);
        Assert.That(cheaper.Balance, Is.EqualTo(3000), "Cheaper templates must also be purchased separately");
        var samePrice = await Buy("same-price", "apartment", "Loft", 1200);
        Assert.That(samePrice.Balance, Is.EqualTo(1800));
        var replay = await Buy("comfort", "apartment", "Comfort", 1200, 4);
        Assert.That(replay.Applied, Is.False);
        Assert.That(replay.Balance, Is.EqualTo(1800));
        var owned = await Buy("back-to-studio", "apartment", "Studio", 500);
        Assert.That(owned.Balance, Is.EqualTo(1800));
        Assert.That(owned.Awarded, Is.Zero);
        Assert.That(owned.State.Apartments, Is.EquivalentTo(new[] { "Studio", "Comfort", "Small", "Loft" }));
        Assert.That(owned.State.ApartmentCredit, Is.Zero);
    }
    [Test]
    public async Task LegacyApartmentCreditDoesNotReduceNewPricesOrRemoveExistingOwnership()
    {
        await Fund(2000);
        await Economy(_user);
        await using (var db = _settings.OpenDatabase())
        {
            var record = await db.LagoonEconomyRecords.SingleAsync(p => p.Id == "account:" + _user.ToString("N"));
            var state = JsonSerializer.Deserialize<LagoonEconomyState>(record.Data)!;
            state.ApartmentCredit = 5000;
            state.Apartments.Add("Studio");
            record.Data = JsonSerializer.Serialize(state);
            await db.SaveChangesAsync();
        }
        var owned = await Buy("old-studio", "apartment", "Studio", 500);
        Assert.That(owned.Balance, Is.EqualTo(2000));
        var comfort = await Buy("new-comfort", "apartment", "Comfort", 1200);
        Assert.That(comfort.Balance, Is.EqualTo(800));
        Assert.That(comfort.Awarded, Is.EqualTo(-1200));
        Assert.That(comfort.State.ApartmentCredit, Is.EqualTo(5000), "Legacy data is preserved but ignored");
        Assert.That(comfort.State.Apartments, Is.EquivalentTo(new[] { "Studio", "Comfort" }));
    }
    [Test]
    public async Task ApartmentPurchaseRejectsInsufficientFullPriceWithoutGrantingOwnership()
    {
        await Fund(1500);
        await Buy("studio", "apartment", "Studio", 500);
        Assert.ThrowsAsync<InsufficientLagoonCoinsException>(async () => await Buy("comfort", "apartment", "Comfort", 1200));
        var fresh = await Economy(_user);
        Assert.That(fresh.Balance, Is.EqualTo(1000));
        Assert.That(fresh.State.Apartments, Is.EquivalentTo(new[] { "Studio" }));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user && p.OperationId == "spend:comfort"), Is.Zero);
    }
    [Test]
    public async Task FurnitureDiscountAndOwnershipArePermanent()
    {
        await Fund(4100);
        var decor = await Buy("decor", "furniture", "Lamp", 101, 4);
        Assert.That(decor.Balance, Is.EqualTo(4014));
        var owned = await Buy("decor-owned", "furniture", "Lamp", 101);
        Assert.That(owned.Balance, Is.EqualTo(4014));
        Assert.That(owned.State.Furniture, Does.Contain("Lamp"));
    }
    [Test]
    public async Task ReferralPaysEachMilestoneOnceAcrossCalendarMonths()
    {
        var now = DateTime.UtcNow.Date.AddHours(12);
        var owner = await Economy(_other, at: now);
        await Economy(_user, at: now);
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.BindReferral(db, _user, owner.State.ReferralCode, now);
        for (var day = 0; day < 5; day++) for (var hour = 0; hour < 3; hour++) await Play(now.AddDays(day), 60);
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.ReferralRewards(db, _user, now.AddDays(7));
        await using (var db = _settings.OpenDatabase()) Assert.That(await AccountEconomyOperations.ReferralRewards(db, _user, now.AddDays(8)), Is.Empty);
        Assert.That((await Economy(_other)).Balance, Is.EqualTo(100));
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(30 + 15 + 50));
    }
}
