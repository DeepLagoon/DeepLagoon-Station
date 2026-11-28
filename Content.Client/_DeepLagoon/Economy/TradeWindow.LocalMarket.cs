using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._NF.Market;
using Content.Shared._NF.Market.BUI;
using Content.Shared._NF.Market.Events;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class TradeWindow
{
    private Action<BoundUserInterfaceMessage>? _localSend;
    private MarketConsoleInterfaceState? _localState;
    private readonly BoxContainer _localRoot = Column(), _localProducts = Column(), _localCart = Column();
    private readonly LineEdit _localSearch = new() { HorizontalExpand = true };
    private readonly Label _localPages = new();
    private Button _localPrevious = default!, _localNext = default!;
    private int _localPage;
    private bool _localConfirm;
    private TimeSpan _localPendingUntil;
    private const int LocalPageSize = 40;
    private bool LocalBusy => _timing.RealTime < _localPendingUntil;
    private long LocalTotal => (_localState?.CartBalance ?? 0) + (_localState?.TransactionCost ?? 0);
    private bool LocalCanPay => _localSend != null && _localState is { Enabled: true, CartDataList.Count: > 0 } &&
        _localState.Balance >= LocalTotal && !LocalBusy;
    private MarketData[] LocalMatches => (_localState?.MarketDataList ?? []).Where(item =>
        _prototypes.TryIndex(item.Prototype, out var proto) && proto.Name.Contains(_localSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => _prototypes.Index(item.Prototype).Name).ThenBy(item => item.Prototype.Id).ToArray();
    private MarketData[] LocalRows => LocalMatches.Skip(_localPage * LocalPageSize).Take(LocalPageSize).ToArray();

    public void ConfigureLocalMarket(Action<BoundUserInterfaceMessage> send)
    {
        _localSend = send;
        _tabs.SetTabVisible(4, true);
        Publish();
    }

    public void UpdateLocalMarket(MarketConsoleInterfaceState state)
    {
        if (_localState != null && (state.CartBalance != _localState.CartBalance || state.TransactionCost != _localState.TransactionCost ||
            !state.CartDataList.Select(i => (i.Prototype, i.Quantity, i.Price)).SequenceEqual(_localState.CartDataList.Select(i => (i.Prototype, i.Quantity, i.Price)))))
            _localConfirm = false;
        _localState = state;
        _localPendingUntil = TimeSpan.Zero;
        _localPage = Math.Min(_localPage, Math.Max(0, (LocalMatches.Length - 1) / LocalPageSize));
        RenderNative(); Publish();
    }

    private void LocalSearch(string value)
    {
        _localSearch.Text = value[..Math.Min(value.Length, 100)];
        _localPage = 0; RenderLocalMarket(); Publish();
    }

    private void LocalCart(string prototype, int amount, bool remove = false)
    {
        if (_localSend == null || _localState is not { Enabled: true } || LocalBusy) return;
        var source = remove ? _localState.CartDataList : _localState.MarketDataList;
        if (!source.Any(item => item.Prototype.Id == prototype)) return;
        if (!remove && amount is not (1 or 5 or 10 or 30 or int.MaxValue)) return;
        _localConfirm = false;
        _localPendingUntil = _timing.RealTime + TimeSpan.FromSeconds(5);
        _localSend(new MarketConsoleCartMessage(remove ? 0 : amount, prototype, remove));
        RenderLocalMarket(); Publish();
    }

    private void LocalPurchase(bool confirmed)
    {
        if (!LocalCanPay) return;
        if (!confirmed) { _localConfirm = true; RenderLocalMarket(); Publish(); return; }
        if (!_localConfirm) return;
        _localConfirm = false;
        _localPendingUntil = _timing.RealTime + TimeSpan.FromSeconds(5);
        _localSend!(new MarketPurchaseMessage());
        RenderLocalMarket(); Publish();
    }

    private void BuildLocalMarket()
    {
        _localRoot.Name = L("local"); _tabs.AddChild(_localRoot); _tabs.SetTabVisible(4, false);
        _localSearch.PlaceHolder = L("search");
        _localRoot.AddChild(_localSearch);
        _localSearch.OnTextChanged += _ => { _localPage = 0; RenderLocalMarket(); Publish(); };
        var panes = Row(); panes.VerticalExpand = true; _localRoot.AddChild(panes);
        panes.AddChild(Scrolling(_localProducts));
        var cart = Scrolling(_localCart); cart.HorizontalExpand = false; cart.SetWidth = 320; panes.AddChild(cart);
        var pages = Row(); _localRoot.AddChild(pages);
        _localPrevious = AddButton(pages, L("prev"), () => { _localPage--; RenderLocalMarket(); Publish(); });
        pages.AddChild(_localPages);
        _localNext = AddButton(pages, L("next"), () => { _localPage++; RenderLocalMarket(); Publish(); });
    }

    private void RenderLocalMarket()
    {
        if (_section != "local") return;
        _localProducts.RemoveAllChildren(); _localCart.RemoveAllChildren();
        var matches = LocalMatches;
        var pages = Math.Max(1, (matches.Length + LocalPageSize - 1) / LocalPageSize);
        _localPage = Math.Clamp(_localPage, 0, pages - 1);
        _localPages.Text = $"{_localPage + 1}/{pages}";
        _localPrevious.Disabled = _localPage == 0;
        _localNext.Disabled = _localPage + 1 >= pages;
        foreach (var item in LocalRows)
        {
            var card = Column(); _localProducts.AddChild(card);
            var row = Row(); card.AddChild(row); row.AddChild(Icon(item.Prototype.Id, 48));
            var words = Column(); row.AddChild(words);
            Text(words, _prototypes.Index(item.Prototype).Name);
            Text(words, $"{Money(LocalPrice(item))} · {L("local-unit")}");
            Text(words, $"{L("local-stock")}: {item.Quantity}");
            var actions = Row(); card.AddChild(actions);
            foreach (var count in new[] { 1, 5, 10, 30, int.MaxValue })
                if (count == int.MaxValue || count <= item.Quantity)
                    AddButton(actions, count == int.MaxValue ? L("local-all") : count.ToString(), () => LocalCart(item.Prototype.Id, count))
                        .Disabled = _localState?.Enabled != true || LocalBusy;
        }
        if (matches.Length == 0) Text(_localProducts, L("local-empty"));
        Text(_localCart, $"{L("local-cart")} · {_localState?.CartEntities ?? 0}/30");
        foreach (var item in _localState?.CartDataList ?? [])
        {
            var row = Row(); _localCart.AddChild(row); row.AddChild(Icon(item.Prototype.Id, 40));
            var words = Column(); row.AddChild(words);
            Text(words, _prototypes.TryIndex(item.Prototype, out var proto) ? proto.Name : item.Prototype.Id);
            Text(words, $"×{item.Quantity} · {Money(LocalPrice(item, item.Quantity))}");
            AddButton(_localCart, L("local-remove"), () => LocalCart(item.Prototype.Id, 0, true)).Disabled = LocalBusy || _localState?.Enabled != true;
        }
        Text(_localCart, $"{L("local-fee")}: {Money(_localState?.TransactionCost ?? 0)}");
        Text(_localCart, $"{L("total-label")}: {Money(LocalTotal)}");
        if (_localConfirm)
        {
            Text(_localCart, L("local-confirm"));
            var actions = Row(); _localCart.AddChild(actions);
            AddButton(actions, L("back"), () => { _localConfirm = false; RenderLocalMarket(); Publish(); });
            AddButton(actions, L("confirm"), () => LocalPurchase(true)).Disabled = !LocalCanPay;
        }
        else AddButton(_localCart, L("local-checkout"), () => LocalPurchase(false)).Disabled = !LocalCanPay;
    }

    private long LocalPrice(MarketData item, int quantity = 1)
        => (long)Math.Clamp(Math.Round(item.Price * (_localState?.MarketModifier ?? 1) * quantity), 0, long.MaxValue);

    private TguiData LocalData() => new TguiData().Bool("available", _localSend != null)
        .Bool("enabled", _localState?.Enabled == true && !LocalBusy).Bool("canPay", LocalCanPay).Bool("confirm", _localConfirm)
        .String("search", _localSearch.Text).String("bank", Money(_localState?.Balance ?? 0))
        .Number("page", _localPage).Number("pages", Math.Max(1, (LocalMatches.Length + LocalPageSize - 1) / LocalPageSize))
        .Number("entities", _localState?.CartEntities ?? 0).String("fee", Money(_localState?.TransactionCost ?? 0)).String("total", Money(LocalTotal))
        .Array("stock", LocalRows.Select(LocalItemData)).Array("cart", (_localState?.CartDataList ?? []).Select(LocalItemData));

    private TguiData LocalItemData(MarketData item) => new TguiData().String("prototype", item.Prototype.Id)
        .String("name", _prototypes.TryIndex(item.Prototype, out var proto) ? proto.Name : item.Prototype.Id)
        .Number("quantity", item.Quantity).String("unitPrice", Money(LocalPrice(item))).String("money", Money(LocalPrice(item, item.Quantity)));
}
