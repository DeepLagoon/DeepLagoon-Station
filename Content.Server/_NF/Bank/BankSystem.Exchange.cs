using System.Threading.Tasks;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Bank.Events;
using Content.Shared.Preferences;
using Robust.Shared.Network;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem
{
    public bool BeginExchangePayment(NetUserId user) => _bankPayments.Add(user);
    public bool IsPaymentPending(NetUserId user) => _bankPayments.Contains(user);
    public void EndExchangePayment(NetUserId user) => _bankPayments.Remove(user);

    /// <summary>Reload all slots without changing the selected character after an asynchronous committed trade.</summary>
    public async Task PublishExchangeBalance(NetUserId user, int slot)
    {
        if (!_playerManager.TryGetSessionById(user, out var session)) return;
        await _prefsManager.RefreshPreferencesAsync(session, default);
        if (!TryGetCharacter(session, out _, out var profile, out var current) || current != slot) return;
        if (session.AttachedEntity is { } actor && HasComp<BankAccountComponent>(actor))
            SetConfirmedBankBalance(actor, profile.BankBalance);
        RaiseLocalEvent(new BalanceChangedEvent(session, profile.BankBalance));
    }
}
