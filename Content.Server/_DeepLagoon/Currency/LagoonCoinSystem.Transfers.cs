using System.Globalization;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Currency;

public sealed partial class LagoonCoinSystem
{
    private static readonly TimeSpan TransferConfirmationTime = TimeSpan.FromMinutes(1);
    private readonly Dictionary<NetUserId, PendingTransfer> _pendingTransfers = new();

    private sealed class PendingTransfer(ICommonSession recipient, long amount, TimeSpan expires)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly ICommonSession Recipient = recipient;
        public readonly long Amount = amount;
        public readonly TimeSpan Expires = expires;
        public bool InProgress;
        public string Code => Id.ToString("N")[..8];
    }

    internal void PrepareTransfer(ICommonSession sender, string recipientName, long amount)
    {
        if (!CheckTransferSender(sender))
            return;

        if (_pendingTransfers.TryGetValue(sender.UserId, out var existing) && existing.InProgress)
        {
            ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-busy"));
            return;
        }

        if (!_players.TryGetSessionByUsername(recipientName, out var recipient) ||
            !recipient.Name.Equals(recipientName, StringComparison.Ordinal) || !IsTransferParticipant(recipient))
        {
            ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-player-unavailable"));
            return;
        }

        if (sender.UserId == recipient.UserId)
        {
            ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-self"));
            return;
        }

        var transfer = new PendingTransfer(recipient, amount, _timing.RealTime + TransferConfirmationTime);
        _pendingTransfers[sender.UserId] = transfer;
        ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-confirm",
            ("player", recipient.Name), ("amount", amount.ToString(CultureInfo.InvariantCulture)),
            ("command", $"/lc:pay {transfer.Code}")));
    }

    internal async Task ConfirmTransfer(ICommonSession sender, string code)
    {
        if (!CheckTransferSender(sender))
            return;

        if (!_pendingTransfers.TryGetValue(sender.UserId, out var transfer) ||
            !transfer.Code.Equals(code, StringComparison.OrdinalIgnoreCase) || transfer.Expires <= _timing.RealTime)
        {
            ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-invalid-code"));
            return;
        }

        if (transfer.InProgress)
        {
            ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-busy"));
            return;
        }

        transfer.InProgress = true;
        await _walletOperations.WaitAsync();
        try
        {
            if (!CheckTransferSender(sender) || !_pendingTransfers.TryGetValue(sender.UserId, out var current) ||
                current != transfer)
                return;

            if (transfer.Expires <= _timing.RealTime)
            {
                _pendingTransfers.Remove(sender.UserId);
                ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-invalid-code"));
                return;
            }

            var recipient = transfer.Recipient;
            if (!IsTransferParticipant(recipient))
            {
                _pendingTransfers.Remove(sender.UserId);
                ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-player-unavailable"));
                return;
            }

            var debitReason = Loc.GetString("lagoon-coin-pay-debit", ("player", recipient.Name));
            var creditReason = Loc.GetString("lagoon-coin-pay-credit", ("player", sender.Name));
            LagoonCoinTransfer result;
            try
            {
                result = await _db.TransferLagoonCoinsAsync(sender.UserId, recipient.UserId, transfer.Id,
                    transfer.Amount, debitReason, creditReason);
            }
            catch (InsufficientLagoonCoinsException)
            {
                RemovePendingTransfer(sender.UserId, transfer);
                ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-insufficient"));
                return;
            }
            catch (OverflowException)
            {
                RemovePendingTransfer(sender.UserId, transfer);
                ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-overflow"));
                return;
            }
            catch (Exception e)
            {
                Log.Error($"Could not confirm Lagoon Coin transfer {transfer.Id:N}: {e}");
                ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-retry", ("command", $"/lc:pay {transfer.Code}")));
                return;
            }

            RemovePendingTransfer(sender.UserId, transfer);
            SendBalance(sender.UserId, result.SenderBalance);
            SendBalance(recipient.UserId, result.RecipientBalance);
            // The pending confirmation is consumed once, including after a retry of an unacknowledged commit.
            NotifyOperation(sender.UserId, new LagoonCoinResult(result.SenderBalance, -transfer.Amount, true), debitReason);
            NotifyOperation(recipient.UserId, new LagoonCoinResult(result.RecipientBalance, transfer.Amount, true), creditReason);
        }
        finally
        {
            transfer.InProgress = false;
            _walletOperations.Release();
        }
    }

    internal void ReplyToTransfer(ICommonSession session, string message)
    {
        if (session.Channel.IsConnected)
            _chat.DispatchServerMessage(session, message, suppressLog: true);
    }

    private void RemovePendingTransfer(NetUserId sender, PendingTransfer transfer)
    {
        if (_pendingTransfers.TryGetValue(sender, out var current) && current == transfer)
            _pendingTransfers.Remove(sender);
    }

    private bool CheckTransferSender(ICommonSession sender)
    {
        if (_ticker.RunLevel == GameRunLevel.InRound && !_roundEnded && IsTransferParticipant(sender))
            return true;

        ReplyToTransfer(sender, Loc.GetString("lagoon-coin-pay-in-round-only"));
        return false;
    }

    private bool IsTransferParticipant(ICommonSession session)
        => session.Status == SessionStatus.InGame && session.Channel.IsConnected && _ticker.UserHasJoinedGame(session);
}
