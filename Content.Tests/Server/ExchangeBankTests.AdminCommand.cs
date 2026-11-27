using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Content.Tests.Server;

public sealed partial class ExchangeBankTests
{
    [TestCase(1500L)]
    [TestCase(-1500L)]
    public async Task AdminBankCommandCommitsOneAuditedAdjustmentAndRetryDoesNotDuplicateIt(long amount)
    {
        var actor = Guid.NewGuid();
        AdminBankAdjustment request;
        await using (var db = new SqliteServerDbContext(_options))
        {
            var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 0);
            request = new(Guid.NewGuid(), 0, profile.Id, profile.BankBalance, amount, actor, "Administrator", "Account");
            var result = await AdminBankOperations.Adjust(db, _user, request);
            Assert.That(result, Is.EqualTo(new AdminBankResult(800000 + amount, true)));
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await AdminBankOperations.Adjust(db, _user, request)).Applied, Is.False);
        }
        await using var read = new SqliteServerDbContext(_options);
        var events = await BankFinancialOperation.Pending(read, false, CancellationToken.None);
        Assert.That(events, Has.Count.EqualTo(1));
        var row = events.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Kind, Is.EqualTo(AdminBankOperations.Kind));
            Assert.That(row.Amount, Is.EqualTo(amount));
            Assert.That(row.Balance, Is.EqualTo(800000 + amount));
            Assert.That(row.UserId, Is.EqualTo(_user));
            Assert.That(row.TransactionId, Is.EqualTo(request.Operation));
        });
        var stored = await read.LagoonEconomyRecords.SingleAsync(r => r.Id == AdminBankOperations.AuditPrefix + request.Operation.ToString("N"));
        var audit = AdminBankOperations.ReadAudit(stored.Data);
        Assert.That(audit, Is.EqualTo(new AdminBankAudit(actor, "Administrator", "Account", 0, request.Profile, 800000, 800000 + amount)));
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 1)).BankBalance, Is.EqualTo(500000));
        await BankFinancialOperation.Acknowledge(read, [request.Operation], CancellationToken.None);
        Assert.That(await BankFinancialOperation.Pending(read, false, CancellationToken.None), Is.Empty);
    }

    [TestCase("insufficient")]
    [TestCase("overflow")]
    [TestCase("balance_changed")]
    [TestCase("profile_changed")]
    public async Task RejectedAdminBankCommandLeavesNeitherMoneyNorNotification(string reason)
    {
        await using (var db = new SqliteServerDbContext(_options))
        {
            var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 0);
            var request = new AdminBankAdjustment(Guid.NewGuid(), 0,
                reason == "profile_changed" ? profile.Id + 1000 : profile.Id,
                reason == "balance_changed" ? profile.BankBalance + 1 : profile.BankBalance,
                reason == "insufficient" ? -800001 : reason == "overflow" ? long.MaxValue : 1500,
                null, "SERVER CONSOLE", "Account");
            Assert.CatchAsync<Exception>(() => AdminBankOperations.Adjust(db, _user, request));
        }
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 0)).BankBalance, Is.EqualTo(800000));
        Assert.That(await read.BankFinancialOperations.CountAsync(), Is.Zero);
        Assert.That(await read.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith(AdminBankOperations.AuditPrefix)), Is.Zero);
    }
}
