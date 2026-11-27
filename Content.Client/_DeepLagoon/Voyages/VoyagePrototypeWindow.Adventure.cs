using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Economy;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Client.UserInterface.Controls;
namespace Content.Client._DeepLagoon.Voyages;
public sealed partial class VoyagePrototypeWindow
{
    private Label _riskStatus = default!;
    private BoxContainer _supplies = default!, _quotePanel = default!;
    private LineEdit _supplySearch = default!;
    private SpinBox _supplyAmount = default!;
    public string SelectedDestination => _selected;
    public VoyageRequest RefreshRequest() => new() { Console = Console!.Value, Action = VoyageAction.Refresh, Destination = _selected,
        Search = _supplySearch.Text, Crew = _crewId is { } id ? Model.CrewIdentifiers.GetValueOrDefault(id) : Guid.Empty };
    private void InitializeAdventure(BoxContainer sidebar)
    {
        _riskStatus = Text(""); sidebar.AddChild(_riskStatus);
        var page = Column(8);
        _supplySearch = new LineEdit { PlaceHolder = "Поиск товара", HorizontalExpand = true }; page.AddChild(_supplySearch);
        var quantity = Row(); quantity.AddChild(Text("Количество"));
        _supplyAmount = new SpinBox { Value = 1 }; _supplyAmount.SetButtons(new() { -1 }, new() { 1 }); _supplyAmount.IsValid = n => n is >= 1 and <= 5;
        quantity.AddChild(_supplyAmount); page.AddChild(quantity);
        _quotePanel = Column(5); page.AddChild(_quotePanel);
        var scroll = new ScrollContainer { HScrollEnabled = false, VerticalExpand = true, HorizontalExpand = true };
        _supplies = Column(6); scroll.AddChild(_supplies); page.AddChild(scroll);
        _details.AddChild(page); _details.SetTabTitle(3, "Закупки");
    }
    private void SendAdventure(VoyageAction action, string product = "", Guid operation = default)
    {
        if (Console is not { } console) return;
        Request?.Invoke(new() { Console = console, Action = action, Destination = _selected, Product = product,
            Operation = operation == Guid.Empty ? Guid.NewGuid() : operation,
            Crew = _crewId is { } id ? Model.CrewIdentifiers.GetValueOrDefault(id) : Guid.Empty,
            Quantity = _supplyAmount.Value, Search = _supplySearch.Text, Quote = Model.Quote?.Id ?? Guid.Empty });
    }
    private void RenderAdventure()
    {
        if (_supplies == null) return;
        _supplies.DisposeAllChildren(); _quotePanel.DisposeAllChildren();
        if (!Model.Live) { _supplies.AddChild(Text("Закупки доступны у действующей консоли")); return; }
        if (!Model.System(_selected).Known) { _supplies.AddChild(Text("Сначала исследуйте сектор")); return; }
        if (Model.Quote is { } quote)
        {
            _quotePanel.AddChild(Text(quote.Name)); _quotePanel.AddChild(Text("Итого: " + Money(quote.Total)));
            var risk = new RichTextLabel { Text = "Риск " + (quote.Risk * 100).ToString("0.0", CultureInfo.InvariantCulture) + "% · при провале теряются груз и экипаж", HorizontalExpand = true, MaxWidth = 330 };
            _quotePanel.AddChild(risk);
            var confirm = new Button { Text = "Подтвердить закупку", HorizontalExpand = true };
            confirm.OnPressed += _ => SendAdventure(VoyageAction.Purchase); _quotePanel.AddChild(confirm);
        }
        foreach (var product in Model.Products)
        {
            var row = Row(); var icon = new TradeItemPreview { SetSize = new Vector2(42) }; icon.SetPrototype(product.Prototype); row.AddChild(icon);
            var text = Column(3); var title = Text(product.Name); title.ToolTip = product.Name; text.AddChild(title); text.AddChild(Text(Money(product.Price))); row.AddChild(text);
            var body = Column(5); body.AddChild(row);
            var buy = new Button { Text = "В корзину закупок", HorizontalExpand = true, Disabled = _cartPending };
            buy.OnPressed += _ => AddImport(product); body.AddChild(buy); _supplies.AddChild(Card(body));
        }
        if (Model.Products.Length == 0 && _selected != VoyageRouteModel.Home) _supplies.AddChild(Text("Товары не найдены"));
    }
}
