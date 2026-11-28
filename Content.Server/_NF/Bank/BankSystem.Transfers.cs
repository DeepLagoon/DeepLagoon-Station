using Content.Server.Database;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Events;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Preferences;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    public ParkingCredit? CreditParkingSale(NetUserId user, int character, long amount, Guid operation)
    {
        if (!_bankPayments.Add(user))
            return null;

        try
        {
            var task = _db.CreditParkingAsync(user, character, amount, operation);
            _tasks.BlockWaitOnTask(task);
            var credit = task.GetAwaiter().GetResult();
            if (_prefsManager.TryGetCachedPreferences(user, out var prefs)
                && prefs.Characters.TryGetValue(credit.Character, out var selected) && selected is HumanoidCharacterProfile profile)
            {
                _prefsManager.SetProfile(user, credit.Character, profile.WithBankBalance(credit.Balance), persist: false)
                    .GetAwaiter().GetResult();
                if (_playerManager.TryGetSessionById(user, out var session) && IsCharacter(session, credit.Character))
                {
                    if (session.AttachedEntity is { } actor && HasComp<BankAccountComponent>(actor))
                        SetConfirmedBankBalance(actor, credit.Balance);
                    RaiseLocalEvent(new BalanceChangedEvent(session, credit.Balance));
                }
            }

            return credit;
        }
        finally
        {
            _bankPayments.Remove(user);
        }
    }

    public bool TryTransfer(ICommonSession sender, ICommonSession recipient, long amount, out BankTransfer result)
    {
        result = default;
        if (amount <= 0 || sender.UserId == recipient.UserId ||
            sender.AttachedEntity is { } from && HasComp<IronmanComponent>(from) ||
            recipient.AttachedEntity is { } to && HasComp<IronmanComponent>(to) ||
            !TryGetCharacter(sender, out _, out var senderProfile, out var senderSlot) ||
            !TryGetCharacter(recipient, out _, out var recipientProfile, out var recipientSlot))
            return false;

        if (!_bankPayments.Add(sender.UserId))
            return false;
        if (!_bankPayments.Add(recipient.UserId))
        {
            _bankPayments.Remove(sender.UserId);
            return false;
        }

        try
        {
            var transfer = _db.TransferBankAsync(sender.UserId, senderSlot, senderProfile.BankBalance,
                recipient.UserId, recipientSlot, recipientProfile.BankBalance, amount,
                expectedSenderProfile: ExpectedProfile(sender, senderSlot), expectedRecipientProfile: ExpectedProfile(recipient, recipientSlot));
            _tasks.BlockWaitOnTask(transfer);
            result = transfer.GetAwaiter().GetResult();
            PublishTransferBalance(sender, senderSlot, senderProfile, result.SenderBalance);
            PublishTransferBalance(recipient, recipientSlot, recipientProfile, result.RecipientBalance);
            _log.Info($"{sender.UserId} transferred {amount} to {recipient.UserId}");
            return true;
        }
        catch (Exception e)
        {
            _log.Warning($"Character bank transfer rejected: {e.Message}");
            return false;
        }
        finally
        {
            _bankPayments.Remove(sender.UserId);
            _bankPayments.Remove(recipient.UserId);
        }
    }

    private void PublishTransferBalance(ICommonSession session, int slot, HumanoidCharacterProfile profile, long balance)
    {
        try
        {
            _prefsManager.SetProfile(session.UserId, slot, profile.WithBankBalance(balance), persist: false)
                .GetAwaiter().GetResult();
            if (session.AttachedEntity is { } entity && IsBodyCharacter(entity, session.UserId.UserId, slot) && HasComp<BankAccountComponent>(entity))
                SetConfirmedBankBalance(entity, balance);
            RaiseLocalEvent(new BalanceChangedEvent(session, balance));
            var refresh = _prefsManager.RefreshPreferencesAsync(session, default);
            _tasks.BlockWaitOnTask(refresh);
            refresh.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Publication failure must not report an already committed transfer as rejected.
            _log.Error($"Could not publish committed character bank transfer for {session.UserId}: {e}");
        }
    }
}
