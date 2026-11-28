using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Voyages;

public sealed partial class VoyagePrototypeWindow
{
    private sealed record CartItem(VoyageCartLine Line, string Name, string Prototype);
    private readonly Dictionary<string, CartItem> _importCart = new(), _deliveryCart = new();
    private BoxContainer _importCartRows = default!, _deliveryCartRows = default!;
    private FancyWindow? _cartConfirmation;
    private bool _waitingCart, _waitingImports, _cartPending, _submittedImports;
    private int _cartVersion, _quotedVersion;
    private Guid _submittedCart, _requestedCart;

    private void InitializeBaskets()
    {
        var tabs = new TabContainer { HorizontalExpand = true, VerticalExpand = true };
        for (var i = 0; i < 2; i++)
        {
            var imports = i == 0;
            var page = Column(8);
            var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
            var rows = Column(6); scroll.AddChild(rows); page.AddChild(scroll);
            if (imports) _importCartRows = rows; else _deliveryCartRows = rows;
            var quote = new Button { Text = "Рассчитать корзину", HorizontalExpand = true };
            quote.OnPressed += _ => QuoteBasket(imports); page.AddChild(quote);
            tabs.AddChild(page); tabs.SetTabTitle(i, imports ? "Закупки" : "Доставки");
        }
        _details.AddChild(tabs); _details.SetTabTitle(4, "Корзины");
        RenderBaskets();
    }
    private void CartChanged()
    {
        _cartVersion++; _waitingCart = false;
        _cartConfirmation?.Close(); _cartConfirmation = null;
        RenderBaskets();
    }
    private void AddImport(VoyageProductView product)
    {
        if (_cartPending) return;
        var key = _selected + ":" + product.Id;
        var quantity = Math.Min(5, (_importCart.GetValueOrDefault(key)?.Line.Quantity ?? 0) + _supplyAmount.Value);
        if (!_importCart.ContainsKey(key) && _importCart.Count >= 32) { _result.Text = "Не более 32 позиций в корзине"; return; }
        _importCart[key] = new(new(_selected, product.Id, "", quantity), product.Name, product.Prototype);
        CartChanged(); _result.Text = "Добавлено в корзину закупок";
    }
    private void AddDelivery(VoyageOrder order)
    {
        if (_cartPending) return;
        if (!_deliveryCart.ContainsKey(order.Id) && _deliveryCart.Count >= 8) { _result.Text = "Не более восьми доставок в корзине"; return; }
        _deliveryCart[order.Id] = new(new(_selected, "", order.Id), Model.Manifests.GetValueOrDefault(order.Id, order.Name), order.Prototype);
        CartChanged(); _result.Text = "Добавлено в корзину доставок"; RenderOrders();
    }
    private void RenderBaskets()
    {
        if (_importCartRows == null) return;
        foreach (var (cart, rows) in new[] { (_importCart, _importCartRows), (_deliveryCart, _deliveryCartRows) })
        {
            rows.DisposeAllChildren();
            if (cart.Count == 0) rows.AddChild(Text("Корзина пуста"));
            foreach (var (key, item) in cart)
            {
                var row = Row();
                var icon = new Content.Client._DeepLagoon.Economy.TradeItemPreview { SetSize = new Vector2(36) }; icon.SetPrototype(item.Prototype); row.AddChild(icon);
                var labels = Column(3); labels.AddChild(Text(Model.System(item.Line.Sector).Name));
                var name = Text(item.Name + (item.Line.Product.Length > 0 ? " ×" + item.Line.Quantity : "")); name.ToolTip = name.Text;
                labels.AddChild(name); row.AddChild(labels);
                var remove = new Button { Text = "×", Disabled = _cartPending };
                remove.OnPressed += _ => { if (_cartPending) return; cart.Remove(key); CartChanged(); }; row.AddChild(remove);
                rows.AddChild(Card(row));
            }
        }
        _details.SetTabTitle(4, "Корзины · " + (_importCart.Count + _deliveryCart.Count));
    }
    private void QuoteBasket(bool imports)
    {
        if (Console is not { } console || _cartPending) return;
        var cart = imports ? _importCart : _deliveryCart;
        if (cart.Count == 0) { _result.Text = "Корзина пуста"; return; }
        _waitingCart = true; _waitingImports = imports; _quotedVersion = _cartVersion;
        _requestedCart = Guid.NewGuid();
        Request?.Invoke(new() { Console = console, Action = imports ? VoyageAction.QuoteImportCart : VoyageAction.QuoteDeliveryCart,
            Operation = _requestedCart, Destination = _selected, Cart = cart.Values.Select(i => i.Line).ToArray() });
    }
    private void ApplyBaskets(VoyageSnapshot snapshot)
    {
        if (_submittedCart != Guid.Empty && snapshot.CartCommitted == _submittedCart)
        {
            (_submittedImports ? _importCart : _deliveryCart).Clear(); _submittedCart = Guid.Empty; _cartPending = false;
            CartChanged();
        }
        else if (snapshot.Status.Length > 0 && (_submittedCart == Guid.Empty || snapshot.CartPending != _submittedCart)) { _cartPending = false; _waitingCart = false; }
        if (_waitingCart && _quotedVersion == _cartVersion && snapshot.CartQuote is { } quote && quote.Id == _requestedCart && quote.Imports == _waitingImports)
        {
            _waitingCart = false; OpenCartConfirmation(quote);
        }
        RenderBaskets();
    }
    private void OpenCartConfirmation(VoyageCartQuoteView quote)
    {
        _cartConfirmation?.Close();
        var window = _cartConfirmation = new FancyWindow { Title = quote.Imports ? "Корзина закупок" : "Корзина доставок",
            MinSize = new Vector2(480, 360), SetSize = new Vector2(700, 540) };
        var page = Column(10); window.ContentsContainer.AddChild(page);
        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        var routes = Column(8); scroll.AddChild(routes); page.AddChild(scroll);
        foreach (var route in quote.Routes)
        {
            var details = Column(4); details.AddChild(Text(route.Name, true));
            var manifest = Text(route.Manifest); manifest.ToolTip = route.Manifest; details.AddChild(manifest);
            details.AddChild(Text(route.Crew + " · ур. " + route.Level + " · " + Math.Ceiling(route.Seconds / 60) + " мин до возвращения"));
            details.AddChild(Text("Риск утраты груза и экипажа: " + (route.Risk * 100).ToString("0.0") + "%"));
            details.AddChild(Text(quote.Imports ? "Закупка: " + Money(route.PurchaseCost) : "Награда при успехе: " + Money(route.Reward)));
            routes.AddChild(Card(details));
        }
        if (quote.HireCount > 0) page.AddChild(Text("Нужно нанять экипажей: " + quote.HireCount + " · " + Money(quote.HireCost)));
        page.AddChild(Text("Стоимость товаров: " + Money(quote.PurchaseCost)));
        page.AddChild(Text("К списанию сейчас: " + Money(quote.HireCost + quote.PurchaseCost), true));
        page.AddChild(Text((quote.Imports ? "Оценка выкупа груза: " : "Награда после возвращения: ") + Money(quote.Imports ? quote.GoodsValue : quote.Reward)));
        var estimate = Text("Результат с учётом стоимости груза, найма и риска: " + Money(quote.EstimatedNet));
        estimate.FontColorOverride = quote.EstimatedNet >= 0 ? Color.LightGreen : Color.LightSalmon; page.AddChild(estimate);
        var confirm = new Button { Text = quote.HireCount > 0 ? "Нанять экипажи и отправить корзину" : "Подтвердить и отправить", HorizontalExpand = true };
        confirm.OnPressed += _ =>
        {
            if (Console is not { } console || _cartPending || _quotedVersion != _cartVersion) return;
            _cartPending = true; _submittedCart = quote.Id; _submittedImports = quote.Imports;
            confirm.Disabled = true;
            Request?.Invoke(new() { Console = console, Action = VoyageAction.ConfirmCart, Quote = quote.Id, Destination = _selected });
            window.Close(); RenderBaskets();
        };
        page.AddChild(confirm);
        window.OnClose += () => { if (_cartConfirmation == window) _cartConfirmation = null; window.Dispose(); };
        window.OpenCentered();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cartConfirmation?.Close(); _cartConfirmation = null; }
        base.Dispose(disposing);
    }
}
