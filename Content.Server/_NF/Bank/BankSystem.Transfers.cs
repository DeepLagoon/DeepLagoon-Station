using Content.Server.Database;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Events;
using Content.Shared.Preferences;
using Robust.Shared.Player;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    public bool TryTransfer(ICommonSession sender, ICommonSession recipient, int amount, out BankTransfer result)
    {
        result = default;
        if (amount <= 0 || sender.UserId == recipient.UserId ||
            sender.AttachedEntity is { } from && HasComp<IronmanComponent>(from) ||
            recipient.AttachedEntity is { } to && HasComp<IronmanComponent>(to) ||
            !_prefsManager.TryGetCachedPreferences(sender.UserId, out var senderPrefs) ||
            !_prefsManager.TryGetCachedPreferences(recipient.UserId, out var recipientPrefs) ||
            senderPrefs.SelectedCharacter is not HumanoidCharacterProfile senderProfile ||
            recipientPrefs.SelectedCharacter is not HumanoidCharacterProfile recipientProfile)
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
            var transfer = _db.TransferBankAsync(sender.UserId, senderPrefs.SelectedCharacterIndex, senderProfile.BankBalance,
                recipient.UserId, recipientPrefs.SelectedCharacterIndex, recipientProfile.BankBalance, amount);
            _tasks.BlockWaitOnTask(transfer);
            result = transfer.GetAwaiter().GetResult();
            PublishTransferBalance(sender, senderPrefs.SelectedCharacterIndex, senderProfile, result.SenderBalance);
            PublishTransferBalance(recipient, recipientPrefs.SelectedCharacterIndex, recipientProfile, result.RecipientBalance);
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

    private void PublishTransferBalance(ICommonSession session, int slot, HumanoidCharacterProfile profile, int balance)
    {
        try
        {
            _prefsManager.SetProfile(session.UserId, slot, profile.WithBankBalance(balance), persist: false)
                .GetAwaiter().GetResult();
            if (session.AttachedEntity is { } entity && HasComp<Content.Shared._NF.Bank.Components.BankAccountComponent>(entity))
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
