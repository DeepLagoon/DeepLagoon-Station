using System.Globalization;
using System.Linq;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared.Database;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class TradeWindow
{
    private sealed record CartEntry(TradeOfferView Offer, int Units);
    private readonly Dictionary<Guid, CartEntry> _cart = new();
    private readonly BoxContainer _sellerList = Column(), _basketBody = Column();
    private readonly BoxContainer _sellerPagesBar = Row();
    private string _groupKey = "", _lotAmount = "1", _autoAmount = "50";
    private int _sellerPage;
    private bool _groupOpen;
    private bool GroupExpanded => _groupOpen || _state.Groups.Length == 0 && _state.Offers.Length > 0;
    private bool GroupLoading => _groupOpen && (_state.Group != _groupKey || _state.SellerPage != _sellerPage);
    private TradeQuoteView? _basketQuote;
    private bool _basketSubmitting;
    private static long EntryCost(TradeOfferView offer, int units) => offer.Splittable
        ? TradePricing.Cost(offer.OriginalPrice, offer.OriginalUnits, offer.StackUnits, units) : offer.Price;
    private static string UnitPrice(long price, int units) => Money(0).Replace("0", ((decimal)price / Math.Max(1, units)).ToString("0.##", CultureInfo.CurrentCulture));
    private void SelectGroup(string key)
    {
        if (!_state.Groups.Any(g => g.Key == key)) return;
        _groupKey = key; _groupOpen = true; _sellerPage = 0; _basketQuote = null; _confirmation = null;
        QueueRefresh(_state.Page); RenderNative(); Publish();
    }
    private void CloseGroup() { _groupOpen = false; _confirmation = null; RenderNative(); Publish(); }
    private void Browse(int page = 0) { _groupOpen = false; _confirmation = null; QueueRefresh(page); RenderNative(); Publish(); }
    private void SellerPage(int page) { _sellerPage = Math.Clamp(page, 0, Math.Max(0, _state.SellerPages - 1)); QueueRefresh(_state.Page); }
    private void AddToBasket()
    {
        if (_pending || Selected is not { Mine: false, BuyOrder: false } offer) return;
        if (!int.TryParse(_lotAmount, out var amount) || amount < 1 || amount > offer.StackUnits || !offer.Splittable && amount != offer.StackUnits)
        { Error("status-invalid_basket"); return; }
        if (!_cart.ContainsKey(offer.Id) && _cart.Count >= 64) { Error("status-basket_limit"); return; }
        _cart[offer.Id] = new(offer, amount); _basketQuote = null;
        ChangeSection("basket", false);
    }
    private void RemoveBasket(Guid id) { _cart.Remove(id); _basketQuote = null; RenderNative(); Publish(); }
    private void QuoteBasket(bool automatic)
    {
        if (_pending) return;
        var request = Query(TradeAction.QuoteBasket);
        if (automatic)
        {
            var group = _state.Groups.FirstOrDefault(g => g.Key == _groupKey);
            if (group == null || group.BuyOrder || TradeCatalogue.Commodity(group.Prototype) == null ||
                !int.TryParse(_autoAmount, out var units) || units is < 1 or > 1000000) { Error("status-invalid_basket"); return; }
            request.Prototype = group.Prototype; request.Quantity = units;
        }
        else
        {
            if (_cart.Count == 0) return;
            request.Lines = _cart.Values.Select(e => new TradeBasketSelection(e.Offer.Id, e.Units)).ToArray(); request.Quantity = 0;
        }
        _basketQuote = null; SendMutation(request);
    }
    private void ConfirmBasket()
    {
        if (_basketQuote == null || _pending) return;
        var request = Query(TradeAction.PurchaseBasket); request.Quote = _basketQuote.Id;
        _basketSubmitting = true; _basketQuote = null; SendMutation(request);
    }
    private void BackBasket() { _basketQuote = null; RenderNative(); Publish(); }
    private void RenderBasket()
    {
        _basketBody.RemoveAllChildren();
        foreach (var entry in _cart.Values.Where(_ => _basketQuote == null))
        {
            var row = Column(); var header = Row(); row.AddChild(header); header.AddChild(Icon(entry.Offer.Prototype, 40));
            Text(header, entry.Offer.Name); Text(row, entry.Offer.Character);
            Text(row, entry.Units + " × " + UnitPrice(entry.Offer.Price, entry.Offer.StackUnits));
            Text(row, Money(EntryCost(entry.Offer, entry.Units)));
            AddButton(row, L("local-remove"), () => RemoveBasket(entry.Offer.Id)).Disabled = _pending; _basketBody.AddChild(row);
        }
        if (_cart.Count == 0 && _basketQuote == null) Text(_basketBody, L("basket-empty"));
        if (_cart.Count > 0 && _basketQuote == null) AddButton(_basketBody, L("basket-quote"), () => QuoteBasket(false)).Disabled = _pending;
        if (_basketQuote is { } quote)
        {
            Text(_basketBody, L("basket-confirm"));
            foreach (var line in quote.Lines) { Text(_basketBody, line.Name + " · " + line.Character + " · ×" + line.Units); Text(_basketBody, Money(line.Price)); }
            Text(_basketBody, L("total-label") + ": " + Money(quote.Total));
            AddButton(_basketBody, L("confirm"), ConfirmBasket).Disabled = _pending || quote.Total > _state.Bank;
            AddButton(_basketBody, L("back"), BackBasket);
        }
    }
}
