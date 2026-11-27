using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed partial class ServerDbManager
{
    public Task<LagoonCoinTransfer> TransferLagoonCoinsAsync(NetUserId sender, NetUserId recipient, Guid transferId,
        long amount, string debitReason, string creditReason, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TransferLagoonCoinsAsync(sender, recipient, transferId, amount,
            debitReason, creditReason, cancel));
    }

    public Task RetryLagoonCoinWebhookAsync(Guid user, string key, Guid owner, DateTime retryAt, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RetryLagoonCoinWebhookAsync(user, key, owner, retryAt, cancel));
    }

    public Task<LagoonCoinOperation?> ClaimLagoonCoinWebhookAsync(Guid owner, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ClaimLagoonCoinWebhookAsync(owner, cancel));
    }

    public Task CompleteLagoonCoinWebhookAsync(Guid user, string key, Guid owner, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CompleteLagoonCoinWebhookAsync(user, key, owner, cancel));
    }

    public Task<long> WithdrawBankAsync(NetUserId user, int slot, long expectedBalance, long amount, CancellationToken cancel = default, RecoveryPayment? recovery = null, int? expectedProfile = null)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.WithdrawBankAsync(user, slot, expectedBalance, amount, cancel, recovery, expectedProfile));
    }

    public Task<long> DepositBankAsync(NetUserId user, int slot, long expectedBalance, long amount, CancellationToken cancel = default,
        IReadOnlyList<SpawnedCashSource>? origins = null, RecoveryRemoval? recovery = null, int? expectedProfile = null)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.DepositBankAsync(user, slot, expectedBalance, amount, cancel, origins, recovery, expectedProfile));
    }

    public Task<BankTransfer> TransferBankAsync(NetUserId sender, int senderSlot, long senderBalance,
        NetUserId recipient, int recipientSlot, long recipientBalance, long amount, CancellationToken cancel = default, int? expectedSenderProfile = null, int? expectedRecipientProfile = null)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TransferBankAsync(sender, senderSlot, senderBalance,
            recipient, recipientSlot, recipientBalance, amount, cancel, expectedSenderProfile, expectedRecipientProfile));
    }

    public Task<long> GetLagoonCoinsAsync(NetUserId user, CancellationToken cancel = default)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetLagoonCoinsAsync(user, cancel));
    }

    public Task<List<LagoonCoinOperation>> GetLagoonCoinHistoryAsync(NetUserId user, int page, CancellationToken cancel = default)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetLagoonCoinHistoryAsync(user, page, cancel));
    }

    public Task<LagoonCoinResult> AwardLagoonCoinsAsync(NetUserId user, string operationId, long amount, string reason,
        NetUserId? actor = null, long playedTicks = 0, long subscriberTicks = 0, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AwardLagoonCoinsAsync(user, operationId, amount, reason, actor,
            playedTicks, subscriberTicks, cancel));
    }
}
