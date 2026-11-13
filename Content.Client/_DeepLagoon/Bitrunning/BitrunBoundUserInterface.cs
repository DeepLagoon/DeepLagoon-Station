using Content.Shared._DeepLagoon.Bitrunning;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._DeepLagoon.Bitrunning;

[UsedImplicitly]
public sealed class BitrunBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private BitrunWindow? _window;
    private BitrunBalanceMessage? _balance;
    private bool _requestedBalance;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<BitrunWindow>();
        _window.ActionRequested += SendAction;
        if (_balance is { } balance)
            _window.SetBalances(balance.Credits, balance.ResearchPoints);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not BitrunUiState bitrun)
            return;

        var preparing = bitrun.PreparingBody is { } body && PlayerManager.LocalEntity is { } local &&
            EntMan.GetNetEntity(local) == body;
        _window?.SetState(bitrun, preparing);
        if (bitrun.Mode == BitrunUiMode.Vendor && !_requestedBalance)
        {
            _requestedBalance = true;
            SendMessage(new BitrunUiMessage(BitrunUiAction.RefreshVendorBalance));
        }
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is not BitrunBalanceMessage balance)
            return;

        _balance = balance;
        _window?.SetBalances(balance.Credits, balance.ResearchPoints);
    }

    private void SendAction(BitrunUiMessage message)
    {
        SendMessage(message);
    }
}
