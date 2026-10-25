using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public abstract partial class ServerDbBase
{
    public async Task RetryLagoonCoinWebhookAsync(Guid user, string key, Guid owner, DateTime retryAt, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await AccountCurrencyOperations.RetryLagoonCoinWebhook(db.DbContext, user, key, owner, retryAt, cancel);
    }

    public async Task<LagoonCoinOperation?> ClaimLagoonCoinWebhookAsync(Guid owner, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db.DbContext, owner, cancel);
    }

    public async Task CompleteLagoonCoinWebhookAsync(Guid user, string key, Guid owner, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await AccountCurrencyOperations.CompleteLagoonCoinWebhook(db.DbContext, user, key, owner, cancel);
    }

    public async Task<int> WithdrawBankAsync(NetUserId user, int slot, int expectedBalance, int amount, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.WithdrawBank(db.DbContext, user.UserId, slot, expectedBalance, amount, cancel);
    }

    public async Task<int> DepositBankAsync(NetUserId user, int slot, int expectedBalance, int amount, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.DepositBank(db.DbContext, user.UserId, slot, expectedBalance, amount, cancel);
    }

    public async Task<BankTransfer> TransferBankAsync(NetUserId sender, int senderSlot, int senderBalance,
        NetUserId recipient, int recipientSlot, int recipientBalance, int amount, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.TransferBank(db.DbContext, sender.UserId, senderSlot, senderBalance,
            recipient.UserId, recipientSlot, recipientBalance, amount, cancel);
    }

    public async Task<long> GetLagoonCoinsAsync(NetUserId user, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await db.DbContext.Preference.Where(p => p.UserId == user.UserId)
            .Select(p => (long?)p.LagoonCoins).SingleOrDefaultAsync(cancel) ?? 0;
    }

    public async Task<List<LagoonCoinOperation>> GetLagoonCoinHistoryAsync(NetUserId user, int page, CancellationToken cancel = default)
    {
        if (page is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.GetLagoonCoinHistory(db.DbContext, user.UserId, page, cancel);
    }

    public async Task<LagoonCoinResult> AwardLagoonCoinsAsync(NetUserId user, string operationId, long amount,
        string reason, NetUserId? actor = null, long playedTicks = 0, long subscriberTicks = 0,
        CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountCurrencyOperations.AwardLagoonCoins(db.DbContext, user.UserId, operationId, amount,
            reason, actor?.UserId, playedTicks, subscriberTicks, cancel);
    }
}
