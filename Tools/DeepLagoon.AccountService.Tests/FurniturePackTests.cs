using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    private async Task<LagoonEconomySnapshot> Pack(string key, int quantity = 1, int size = 6, long price = 35, long expected = 35, int tier = 0)
    {
        await using var db = _settings.OpenDatabase();
        return await AccountEconomyOperations.Purchase(db, _user, new(key, "furniture-pack", "ChairSet", price, tier, quantity, size, expected), DateTime.UtcNow);
    }

    [Test]
    public async Task FurniturePacksPersistQuantitiesAndRepeatedOrdersCannotChargeOrGrantAgain()
    {
        await Fund(1000);
        var first = await Pack("pack-first", 2);
        Assert.That(first.Balance, Is.EqualTo(930)); Assert.That(first.State.FurnitureStock["ChairSet"], Is.EqualTo(12));
        var duplicate = await Pack("pack-first", 2);
        Assert.That(duplicate.Applied, Is.False); Assert.That(duplicate.Balance, Is.EqualTo(930));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Pack("pack-first", 3));
        var later = await Pack("pack-later");
        Assert.That(later.Balance, Is.EqualTo(895)); Assert.That(later.State.FurnitureStock["ChairSet"], Is.EqualTo(18));
        var restored = await Economy(_user);
        Assert.That(restored.State.FurnitureStock["ChairSet"], Is.EqualTo(18));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Preference.Where(p => p.UserId == _user).Select(p => p.Money).SingleAsync(), Is.EqualTo(777));
    }

    [Test]
    public async Task FurniturePackInsufficientFundsInvalidQuantityAndAbortedCommitLeaveNoReceiptOrStock()
    {
        await Fund(50);
        Assert.ThrowsAsync<InsufficientLagoonCoinsException>(async () => await Pack("pack-poor", 2));
        Assert.ThrowsAsync<ArgumentException>(async () => await Pack("pack-zero", 0));
        Assert.ThrowsAsync<OverflowException>(async () => await Pack("pack-overflow", int.MaxValue));
        await using (var db = _settings.OpenDatabase())
            Assert.ThrowsAsync<InvalidOperationException>(async () => await AccountEconomyOperations.Purchase(db, _user,
                new("pack-abort", "furniture-pack", "ChairSet", 35, 0, 1, 6, 35), DateTime.UtcNow, beforeCommit: () => throw new InvalidOperationException("abort")));
        var snapshot = await Economy(_user);
        Assert.That(snapshot.Balance, Is.EqualTo(50)); Assert.That(snapshot.State.FurnitureStock, Is.Empty);
        await using var check = _settings.OpenDatabase();
        Assert.That(await check.LagoonEconomyRecords.CountAsync(p => p.Id.StartsWith("receipt:pack-")), Is.Zero);
    }

    [Test]
    public async Task FurniturePackUsesDiscountPerPackAndRejectsAnOutdatedQuote()
    {
        await Fund(1000);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Pack("pack-old-quote", price: 101, expected: 101, tier: 4));
        var fresh = await Pack("pack-current-quote", 2, price: 101, expected: 86, tier: 4);
        Assert.That(fresh.Balance, Is.EqualTo(828)); Assert.That(fresh.State.FurnitureStock["ChairSet"], Is.EqualTo(12));
    }

    [Test]
    public async Task AnOlderAccountStateWriterCannotErasePurchasedFurnitureStock()
    {
        await Fund(100);
        await Pack("pack-before-old-writer");
        await using (var db = _settings.OpenDatabase())
        {
            var key = "account:" + _user.ToString("N");
            var record = await db.LagoonEconomyRecords.SingleAsync(item => item.Id == key);
            using var json = System.Text.Json.JsonDocument.Parse(record.Data);
            record.Data = System.Text.Json.JsonSerializer.Serialize(json.RootElement.EnumerateObject()
                .Where(item => item.Name != "FurnitureStock").ToDictionary(item => item.Name, item => item.Value.Clone()));
            await db.SaveChangesAsync();
        }
        var restored = await Economy(_user);
        Assert.That(restored.State.FurnitureStock["ChairSet"], Is.EqualTo(6));
        Assert.That(restored.Balance, Is.EqualTo(65));
        var later = await Pack("pack-after-old-writer");
        Assert.That(later.State.FurnitureStock["ChairSet"], Is.EqualTo(12));
        Assert.That(later.Balance, Is.EqualTo(30));
    }

    [Test]
    public async Task FurnitureFreePacksAddCapacityWithoutDebitingCoins()
    {
        var result = await Pack("pack-free", 3, size: 4, price: 0, expected: 0);
        Assert.That(result.Balance, Is.Zero); Assert.That(result.State.FurnitureStock["ChairSet"], Is.EqualTo(12));
    }

    [Test]
    public async Task ConcurrentFurnitureOrdersShareTheWalletLockAndDuplicateReceipts()
    {
        await Fund(100);
        var repeats = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Pack("pack-concurrent"))));
        Assert.That(repeats.Count(item => item.Applied), Is.EqualTo(1));
        var current = await Economy(_user);
        Assert.That(current.Balance, Is.EqualTo(65)); Assert.That(current.State.FurnitureStock["ChairSet"], Is.EqualTo(6));
        async Task<bool> Spend(string key)
        {
            try { await Pack(key); return true; }
            catch (InsufficientLagoonCoinsException) { return false; }
        }
        var separate = await Task.WhenAll(Task.Run(() => Spend("pack-race-a")), Task.Run(() => Spend("pack-race-b")));
        Assert.That(separate.Count(value => value), Is.EqualTo(1));
        current = await Economy(_user);
        Assert.That(current.Balance, Is.EqualTo(30)); Assert.That(current.State.FurnitureStock["ChairSet"], Is.EqualTo(12));
    }
}
