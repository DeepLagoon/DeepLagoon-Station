using Content.Shared._NF.Market.BUI;
using JetBrains.Annotations;

namespace Content.Client._NF.Market.BUI;

[UsedImplicitly]
public sealed class MarketConsoleBoundUserInterface : BoundUserInterface
{
    private Content.Client._DeepLagoon.Economy.TradeWindow? _menu;

    [ViewVariables]
    public long BankBalance { get; private set; }

    public MarketConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey) { }

    protected override void Open()
    {
        base.Open();

        _menu = EntMan.System<Content.Client._DeepLagoon.Economy.PlayerTradeClientSystem>().Open(Owner, SendMessage);
        _menu.OnClose += Close;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not MarketConsoleInterfaceState uiState)
            return;

        if (_menu == null)
            return;

        BankBalance = uiState.Balance;

        _menu.UpdateLocalMarket(uiState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        EntMan.System<Content.Client._DeepLagoon.Economy.PlayerTradeClientSystem>().Close(Owner);
        _menu?.Dispose();
    }
}
