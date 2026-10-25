using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public readonly record struct BankTransfer(int SenderBalance, int RecipientBalance);
public readonly record struct LagoonCoinResult(long Balance, long Awarded, bool Applied);

public sealed class InsufficientLagoonCoinsException : InvalidOperationException
{
    public InsufficientLagoonCoinsException() : base("Insufficient Lagoon Coin balance.") { }
}

/// <summary>All writers lock the preference row before reading it. Works across server processes.</summary>
public static class AccountCurrencyOperations
{
    public static async Task<LagoonCoinOperation?> ClaimLagoonCoinWebhook(ServerDbContext db, Guid owner,
        CancellationToken cancel = default)
    {
        var now = DateTime.UtcNow;
        var pending = db.LagoonCoinOperations.AsNoTracking().Where(p => !p.WebhookDelivered &&
            (p.WebhookLeaseUntil == null || p.WebhookLeaseUntil < now));
        var row = await pending.OrderBy(p => p.CreatedAt).ThenBy(p => p.OperationId).FirstOrDefaultAsync(cancel);
        if (row == null) return null;
        var updated = await pending.Where(p => p.UserId == row.UserId && p.OperationId == row.OperationId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookLeaseOwner, (Guid?)owner)
                .SetProperty(p => p.WebhookLeaseUntil, (DateTime?)now.AddMinutes(2)), cancel);
        return updated == 1 ? row : null;
    }

    public static async Task CompleteLagoonCoinWebhook(ServerDbContext db, Guid user, string key, Guid owner,
        CancellationToken cancel = default)
    {
        await db.LagoonCoinOperations.Where(p => p.UserId == user && p.OperationId == key && p.WebhookLeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookDelivered, true)
                .SetProperty(p => p.WebhookLeaseOwner, (Guid?)null).SetProperty(p => p.WebhookLeaseUntil, (DateTime?)null), cancel);
    }

    public static async Task RetryLagoonCoinWebhook(ServerDbContext db, Guid user, string key, Guid owner,
        DateTime retryAt, CancellationToken cancel = default)
    {
        await db.LagoonCoinOperations.Where(p => p.UserId == user && p.OperationId == key &&
                !p.WebhookDelivered && p.WebhookLeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookLeaseOwner, (Guid?)null)
                .SetProperty(p => p.WebhookLeaseUntil, (DateTime?)retryAt), cancel);
    }
    public static async Task<System.Collections.Generic.List<LagoonCoinOperation>> GetLagoonCoinHistory(
        ServerDbContext db, Guid user, int page, CancellationToken cancel = default)
    {
        if (page is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(page));
        return await db.LagoonCoinOperations.AsNoTracking().Where(p => p.UserId == user && p.Amount != 0)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.OperationId)
            .Skip((page - 1) * 20).Take(21).ToListAsync(cancel);
    }

    public static async Task LockAccount(ServerDbContext db, Guid user, CancellationToken cancel = default)
    {
        // An UPDATE takes a row lock in PostgreSQL and a writer lock in SQLite. No cached balance is trusted.
        var count = await db.Preference.Where(p => p.UserId == user)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LagoonCoins, p => p.LagoonCoins), cancel);
        if (count != 1)
            throw new InvalidOperationException("Player preferences do not exist.");
    }

    public static async Task<int> WithdrawBank(ServerDbContext db, Guid user, int slot, int expectedBalance, int amount,
        CancellationToken cancel = default)
    {
        if (amount <= 0 || expectedBalance < amount)
            throw new InvalidOperationException("Insufficient character bank balance.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await LockAccount(db, user, cancel);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        if (profile.BankBalance != expectedBalance)
            throw new InvalidOperationException("Character bank balance changed; retry the payment.");
        profile.BankBalance -= amount;
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
        return profile.BankBalance;
    }

    public static async Task<BankTransfer> TransferBank(ServerDbContext db, Guid sender, int senderSlot,
        int senderBalance, Guid recipient, int recipientSlot, int recipientBalance, int amount,
        CancellationToken cancel = default)
    {
        if (sender == recipient || amount <= 0)
            throw new ArgumentException("A transfer requires two different accounts and a positive amount.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        // Deterministic locking order prevents opposite-direction transfers from deadlocking.
        await LockAccount(db, sender.CompareTo(recipient) < 0 ? sender : recipient, cancel);
        await LockAccount(db, sender.CompareTo(recipient) < 0 ? recipient : sender, cancel);
        var from = await db.Profile.SingleAsync(p => p.Preference.UserId == sender && p.Slot == senderSlot, cancel);
        var to = await db.Profile.SingleAsync(p => p.Preference.UserId == recipient && p.Slot == recipientSlot, cancel);
        if (from.BankBalance != senderBalance || to.BankBalance != recipientBalance)
            throw new InvalidOperationException("Character bank balance changed; retry the transfer.");
        if (from.BankBalance < amount || to.BankBalance < 0)
            throw new InvalidOperationException("Insufficient character bank balance.");
        var balance = checked(to.BankBalance + amount);
        from.BankBalance -= amount;
        to.BankBalance = balance;
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
        return new BankTransfer(from.BankBalance, to.BankBalance);
    }

    public static async Task<int> DepositBank(ServerDbContext db, Guid user, int profileSlot,
        int expectedBankBalance, int amount, CancellationToken cancel = default)
    {
        if (amount <= 0 || expectedBankBalance < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await LockAccount(db, user, cancel);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == profileSlot, cancel);
        if (profile.BankBalance != expectedBankBalance)
            throw new InvalidOperationException("Character bank balance changed; retry the deposit.");
        profile.BankBalance = checked(profile.BankBalance + amount);
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
        return profile.BankBalance;
    }

    public static async Task<LagoonCoinResult> AwardLagoonCoins(ServerDbContext db, Guid user,
        string operationId, long amount, string reason, Guid? actorId = null, long playedTicks = 0,
        long subscriberTicks = 0, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 200 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 500 ||
            playedTicks < 0 || subscriberTicks < 0 || subscriberTicks > playedTicks || (playedTicks > 0 && amount != 0))
            throw new ArgumentException("Invalid Lagoon Coin operation.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await LockAccount(db, user, cancel);
        var row = await db.Preference.SingleAsync(p => p.UserId == user, cancel);
        var previous = await db.LagoonCoinOperations.SingleOrDefaultAsync(
            p => p.UserId == user && p.OperationId == operationId, cancel);
        if (previous != null)
        {
            if (previous.PlayedTicks != playedTicks || previous.SubscriberTicks != subscriberTicks ||
                previous.Reason != reason || previous.ActorId != actorId || (playedTicks == 0 && previous.Amount != amount))
                throw new InvalidOperationException("Lagoon Coin operation key was reused with different data.");
            return new LagoonCoinResult(row.LagoonCoins, 0, false);
        }
        var played = checked(row.LagoonCoinPlayedTicks + playedTicks);
        var bonus = checked(row.LagoonCoinBonusTicks + subscriberTicks);
        var hours = played / TimeSpan.TicksPerHour;
        if (hours > 0)
        {
            amount = checked(amount + hours * 2 + bonus / (TimeSpan.TicksPerHour / 2));
            played %= TimeSpan.TicksPerHour;
            bonus %= TimeSpan.TicksPerHour / 2;
        }
        var balance = checked(row.LagoonCoins + amount);
        if (balance < 0)
            throw new InsufficientLagoonCoinsException();
        row.LagoonCoins = balance;
        row.LagoonCoinPlayedTicks = played;
        row.LagoonCoinBonusTicks = bonus;
        db.LagoonCoinOperations.Add(new LagoonCoinOperation
        {
            UserId = user, OperationId = operationId, Amount = amount, Reason = reason,
            ActorId = actorId, PlayedTicks = playedTicks, SubscriberTicks = subscriberTicks, CreatedAt = DateTime.UtcNow,
            BalanceAfter = row.LagoonCoins, WebhookDelivered = amount == 0,
        });
        await db.SaveChangesAsync(cancel);
        beforeCommit?.Invoke();
        await transaction.CommitAsync(cancel);
        return new LagoonCoinResult(row.LagoonCoins, amount, true);
    }
}
