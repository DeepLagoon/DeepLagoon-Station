using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Content.Tests.Server;

public sealed partial class ExchangeBankTests
{
    [Test]
    public async Task PasswordChangeRequiresCurrentPasswordAndRevokesOldFinancialAccessWithoutLosingReceipts()
    {
        await using var db = new SqliteServerDbContext(_options);
        var initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var recipient = await ExchangeOperations.Read(db, _user, 1, 0);
        var access = await ExchangeOperations.LoginAccess(db, _user, 0, new(initial.Address, "original-secret", initial.ProfileId));
        var purchase = new ExchangeTradeRequest(Guid.NewGuid(), 0, "contrib", 1000, true, long.MaxValue,
            initial.ProfileId, WalletAddress: access.Address, WalletVersion: access.Version);
        var funded = await ExchangeOperations.Trade(db, _user, purchase, 1);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.ChangePassword(db, _user, 0,
            new(access.Address, initial.ProfileId, "wrong-password", "replacement-secret")));
        Assert.That((await ExchangeOperations.Read(db, _user, 0, 2)).WalletVersion, Is.EqualTo(access.Version));
        var changed = await ExchangeOperations.ChangePassword(db, _user, 0,
            new(access.Address, initial.ProfileId, "original-secret", "replacement-secret"));
        Assert.That(changed, Is.Not.EqualTo(access.Version));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Login(db, _user, 0,
            new(access.Address, "original-secret", initial.ProfileId)));
        Assert.That((await ExchangeOperations.LoginAccess(db, _user, 0,
            new(access.Address, "replacement-secret", initial.ProfileId))).Version, Is.EqualTo(changed));
        // A confirmed payment remains replayable, even if its acknowledgement was lost before rotation.
        Assert.That((await ExchangeOperations.Trade(db, _user, purchase, 3)).Applied, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Trade(db, _user,
            purchase with { Operation = Guid.NewGuid() }, 4));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Transfer(db, _user,
            new(Guid.NewGuid(), 0, initial.ProfileId, recipient.Address, recipient.ProfileId, "contrib", 500,
                access.Address, access.Version), 4));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Deposit(db, _user, 0,
            "contrib", 500, Guid.NewGuid(), null, 4, walletAddress: access.Address, walletVersion: access.Version));
        var final = await ExchangeOperations.Read(db, _user, 0, 5);
        Assert.That(final.Bank, Is.EqualTo(funded.Bank));
        Assert.That(final.Wallet["contrib"], Is.EqualTo(1000));
        var hash = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == "exchange:password:" + access.Address);
        Assert.That(hash.Data, Does.Not.Contain("original-secret").And.Not.Contain("replacement-secret"));
    }

    [Test]
    public async Task KnownCurrentPasswordCanRotateAForeignWalletWithoutChangingItsOwnerOrAddress()
    {
        await using var db = new SqliteServerDbContext(_options);
        var owner = await ExchangeOperations.Read(db, _user, 0, 0);
        var actor = await ExchangeOperations.Read(db, _user, 1, 0);
        await ExchangeOperations.Login(db, _user, 0, new(owner.Address, "owners-password", owner.ProfileId));
        await ExchangeOperations.ChangePassword(db, _user, 1, new(owner.Address, actor.ProfileId, "owners-password", "new-password"));
        Assert.That(await ExchangeOperations.Login(db, _user, 0, new(owner.Address, "new-password", owner.ProfileId)), Is.EqualTo(owner.Address));
        Assert.That((await ExchangeOperations.Read(db, _user, 0, 1)).Address, Is.EqualTo(owner.Address));
        Assert.That((await ExchangeOperations.Read(db, _user, 1, 1)).Address, Is.EqualTo(actor.Address));
    }

    [Test]
    public async Task WalletAddressesPasswordsAndAdditionalWalletsSurviveReopening()
    {
        ExchangeResult first;
        await using (var db = new SqliteServerDbContext(_options)) first = await ExchangeOperations.Read(db, _user, 0, 0);
        Assert.That(first.Address, Does.Match("^[A-Z0-9]{6}$"));
        Assert.That(first.NeedsPassword, Is.True);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(await ExchangeOperations.Login(db, _user, 0, new(first.Address, "wallet-secret", first.ProfileId)), Is.EqualTo(first.Address));
        string extra;
        await using (var db = new SqliteServerDbContext(_options))
            extra = await ExchangeOperations.Login(db, _user, 0, new("", "extra-secret", first.ProfileId, true));
        Assert.That(extra, Is.Not.EqualTo(first.Address));
        await using (var db = new SqliteServerDbContext(_options))
        {
            var result = await ExchangeOperations.Read(db, _user, 0, 0);
            Assert.That(result.Address, Is.EqualTo(first.Address));
            Assert.That(result.NeedsPassword, Is.False);
            Assert.That(result.Wallets.Select(w => w.Address), Is.EquivalentTo(new[] { extra, first.Address }));
            var another = await ExchangeOperations.Read(db, _user, 1, 0);
            Assert.That(another.Address, Is.Not.EqualTo(first.Address).And.Not.EqualTo(extra));
            Assert.That(another.Wallets, Has.Length.EqualTo(1));
            var passwordRows = await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith("exchange:password:")).ToArrayAsync();
            Assert.That(passwordRows.All(r => !r.Data.Contains("secret")), Is.True);
        }
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Login(db, _user, 0,
                new(first.Address, "wrong-password", first.ProfileId)));
    }

    [Test]
    public async Task TransfersBetweenAdditionalWalletsAreAtomicAndRetryDoesNotMoveCoinsTwice()
    {
        await using var setup = new SqliteServerDbContext(_options);
        var first = await ExchangeOperations.Read(setup, _user, 0, 0);
        await ExchangeOperations.Login(setup, _user, 0, new(first.Address, "first-secret", first.ProfileId));
        var extra = await ExchangeOperations.Login(setup, _user, 0, new("", "second-secret", first.ProfileId, true));
        await ExchangeOperations.Deposit(setup, _user, 0, "contrib", 1000, Guid.NewGuid(), null, 1);
        var request = new ExchangeTransferRequest(Guid.NewGuid(), 0, first.ProfileId, extra, first.ProfileId, "contrib", 333, first.Address);
        await ExchangeOperations.Transfer(setup, _user, request, 2);
        await using var retry = new SqliteServerDbContext(_options);
        var result = await ExchangeOperations.Transfer(retry, _user, request, 3);
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Wallet["contrib"], Is.EqualTo(667));
        var target = await ExchangeOperations.Read(retry, _user, 0, 3, walletAddress: extra);
        Assert.That(target.Wallet["contrib"], Is.EqualTo(333));
        Assert.That(target.Bank, Is.EqualTo(800000));
        Assert.That(await retry.BankFinancialOperations.CountAsync(r => r.Kind == "crypto_transfer"), Is.EqualTo(1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Transfer(retry, _user,
            request with { Operation = Guid.NewGuid(), Units = 668 }, 4));
    }

    [Test]
    public async Task ForeignWalletRequiresItsPasswordAndCashSaleNeverCreditsTheOwnersOrActorsBank()
    {
        await using var db = new SqliteServerDbContext(_options);
        var actor = await ExchangeOperations.Read(db, _user, 0, 0);
        var owner = await ExchangeOperations.Read(db, _user, 1, 0);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Login(db, _user, 0,
            new(owner.Address, "attempt-to-set", actor.ProfileId)));
        await ExchangeOperations.Login(db, _user, 1, new(owner.Address, "owners-secret", owner.ProfileId));
        Assert.That(await ExchangeOperations.Login(db, _user, 0, new(owner.Address, "owners-secret", actor.ProfileId)), Is.EqualTo(owner.Address));
        var bought = await ExchangeOperations.Trade(db, _user,
            new(Guid.NewGuid(), 0, "contrib", 2000, true, long.MaxValue, actor.ProfileId, WalletAddress: owner.Address), 1);
        Assert.That((await ExchangeOperations.Read(db, _user, 0, 1)).Wallet, Is.Empty);
        var proceeds = bought.Market.Quote("contrib", 1000).SellProceeds;
        var sale = new ExchangeTradeRequest(Guid.NewGuid(), 0, "contrib", 1000, false, proceeds, actor.ProfileId, true,
            WalletAddress: owner.Address);
        var sold = await ExchangeOperations.Trade(db, _user, sale, 2);
        Assert.That(sold.Bank, Is.EqualTo(bought.Bank));
        Assert.That(sold.Wallet["contrib"], Is.EqualTo(1000));
        Assert.That(sold.Withdrawals, Has.Length.EqualTo(1));
        Assert.That(sold.Withdrawals[0].Amount, Is.EqualTo(proceeds));
        Assert.That(sold.Withdrawals[0].Cash, Is.True);
        Assert.That(sold.Withdrawals[0].Operation, Is.EqualTo(sale.Operation));
        Assert.That((await ExchangeOperations.Read(db, _user, 1, 2)).Bank, Is.EqualTo(500000));
        Assert.That((await ExchangeOperations.Trade(db, _user, sale, 3)).Applied, Is.False);
        Assert.That((await ExchangeOperations.Read(db, _user, 0, 3, walletAddress: owner.Address)).Withdrawals, Has.Length.EqualTo(1));
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id.StartsWith(ExchangeOperations.AuditPrefix) && r.Data.Contains(sale.Operation.ToString()));
        var audit = JsonSerializer.Deserialize<ExchangeAudit>(row.Data)!;
        Assert.That(audit.Address, Is.EqualTo(owner.Address));
        Assert.That(audit.Payment, Is.EqualTo("cash"));
        Assert.That(audit.Fee, Is.GreaterThan(0));
        Assert.That(await db.BankFinancialOperations.CountAsync(r => r.Kind == "crypto_sell"), Is.EqualTo(1));
        var actorProfile = await db.Profile.SingleAsync(p => p.Id == actor.ProfileId);
        actorProfile.CharacterName = "Bank recipient";
        await db.SaveChangesAsync();
        var bankSale = await ExchangeOperations.Trade(db, _user,
            new(Guid.NewGuid(), 0, "contrib", 500, false, 0, actor.ProfileId, WalletAddress: owner.Address), 4);
        Assert.That(bankSale.Withdrawals, Has.Length.EqualTo(2));
        Assert.That(bankSale.Withdrawals[0].AccountOwner, Is.EqualTo("Bank recipient"));
        Assert.That(bankSale.Withdrawals[0].Cash, Is.False);
        Assert.That(bankSale.Bank - sold.Bank, Is.EqualTo(bankSale.Withdrawals[0].Amount));
    }

    [Test]
    public async Task ConcurrentTransfersCannotOverspendTheSameWallet()
    {
        ExchangeResult sender, receiver;
        await using (var setup = new SqliteServerDbContext(_options))
        {
            sender = await ExchangeOperations.Read(setup, _user, 0, 0);
            receiver = await ExchangeOperations.Read(setup, _user, 1, 0);
            await ExchangeOperations.Deposit(setup, _user, 0, "ideas", 1000, Guid.NewGuid(), null, 1);
        }
        async Task<bool> Transfer()
        {
            await using var db = new SqliteServerDbContext(_options);
            try
            {
                await ExchangeOperations.Transfer(db, _user, new(Guid.NewGuid(), 0, sender.ProfileId,
                    receiver.Address, receiver.ProfileId, "ideas", 600, sender.Address), 2);
                return true;
            }
            catch (InvalidOperationException e) when (e.Message == "insufficient_crypto") { return false; }
        }
        Assert.That((await Task.WhenAll(Task.Run(Transfer), Task.Run(Transfer))).Count(success => success), Is.EqualTo(1));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await ExchangeOperations.Read(read, _user, 0, 3)).Wallet["ideas"], Is.EqualTo(400));
        Assert.That((await ExchangeOperations.Read(read, _user, 1, 3)).Wallet["ideas"], Is.EqualTo(600));
    }
}
