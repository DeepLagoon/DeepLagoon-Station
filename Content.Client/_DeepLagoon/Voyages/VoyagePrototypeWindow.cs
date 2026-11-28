using Content.Shared._DeepLagoon.Voyages;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Economy;
using Content.Client.Stylesheets;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Voyages;

public sealed partial class VoyagePrototypeWindow : DefaultWindow
{
    public VoyagePrototype Model { get; } = new();
    public VoyageMapControl Map { get; }
    private readonly Label _summary, _systemName, _systemRole, _selection, _result;
    private readonly BoxContainer _orders, _flights, _crews;
    private readonly OptionButton _crewPicker;
    private int? _crewId;
    private readonly TabContainer _details;
    private readonly Button _dispatch;
    private readonly Dictionary<string, Label> _budgetLabels = new();
    private readonly Dictionary<VoyageFlight, Label> _flightValues = new();
    private string _selected = VoyagePrototype.Home;
    private VoyageOrder? _order;
    private int _revision = -1, _flightCount = -1;
    private TimeSpan _lastFrame, _nextText;
    private readonly IGameTiming _clock = IoCManager.Resolve<IGameTiming>();

    public NetEntity? Console { get; }
    public event Action<VoyageRequest>? Request;
    private Guid? _selectedCrew;
    public void Apply(VoyageSnapshot snapshot)
    {
        if (snapshot.Origin.Length == 0) { _result.Text = snapshot.Status; ApplyBaskets(snapshot); RenderOrders(); RenderAdventure(); return; }
        var previousRevision = Model.Revision;
        Model.Apply(snapshot);
        ApplyBaskets(snapshot);
        if (_selectedCrew is { } selected) _crewId = Model.CrewIdentifiers.Where(c => c.Value == selected).Select(c => (int?)c.Key).FirstOrDefault();
        if (previousRevision != Model.Revision) { RenderCrews(); RenderOrders(); RenderAdventure(); }
        foreach (var budget in Model.Budgets)
            if (_budgetLabels.TryGetValue(budget.Key, out var value)) value.Text = budget.Key + ": " + Money(budget.Value);
        if (snapshot.Status.Length > 0) _result.Text = snapshot.Status;
    }
    private void Send(VoyageAction action, string offer = "")
    {
        if (Console is not { } console) return;
        var operation = Guid.NewGuid();
        if (action == VoyageAction.Hire) _selectedCrew = operation;
        Request?.Invoke(new VoyageRequest { Console = console, Operation = operation, Action = action, Offer = offer,
            Order = _order?.Id ?? "", Destination = _selected, Survey = !Model.System(_selected).Known,
            Crew = _crewId is { } id ? Model.CrewIdentifiers.GetValueOrDefault(id) : Guid.Empty });
    }
    public VoyagePrototypeWindow(NetEntity? console = null)
    {
        Console = console; if (console != null) Model.UseServer();
        Title = console == null ? "Консоль рейсов · прототип" : "Консоль рейсов";
        MinSize = new Vector2(920, 640); SetSize = new Vector2(1240, 790);
        var root = Column(10); Contents.AddChild(root);
        var header = Row();
        var title = Text("СЕКТОР / ЛОГИСТИКА", true); title.FontColorOverride = Color.FromHex("#6AE1D5"); header.AddChild(title);
        header.AddChild(new Label { Text = console == null ? "ПРОТОТИП" : "", FontColorOverride = Color.FromHex("#BAA8FF") }); root.AddChild(header);
        _summary = Text(""); root.AddChild(_summary);
        var toolbar = Row();
        var demand = new CheckBox { Text = "Товары", Pressed = true };
        var unknown = new CheckBox { Text = "Неизученное", Pressed = true };
        toolbar.AddChild(demand); toolbar.AddChild(unknown);
        var center = new Button { Text = "Центр" }; var zoomIn = new Button { Text = "+" }; var zoomOut = new Button { Text = "−" };
        toolbar.AddChild(center); toolbar.AddChild(zoomIn); toolbar.AddChild(zoomOut);
        var overview = new Button { Text = "Весь сектор" }; toolbar.AddChild(overview);
        var pause = new Button { Text = "Пауза", ToggleMode = true }; toolbar.AddChild(pause);
        var speed = new OptionButton(); foreach (var value in new[] { "×1", "×8", "×32" }) speed.AddItem(value); speed.SelectId(1);
        speed.OnItemSelected += e => { speed.SelectId(e.Id); Model.Speed = new[] { 1, 8, 32 }[e.Id]; }; toolbar.AddChild(speed);
        pause.Visible = speed.Visible = console == null;
        root.AddChild(toolbar);
        var body = Row(); body.VerticalExpand = true; root.AddChild(body);
        Map = new VoyageMapControl(Model); body.AddChild(Map);
        overview.OnPressed += _ => { Map.Offset = Map.TargetOffset = Vector2.Zero; Map.ShowSector(); };
        Map.SelectedSystem += SelectSystem;
        demand.OnToggled += e => Map.ShowDemand = e.Pressed; unknown.OnToggled += e => Map.ShowUnknown = e.Pressed;
        center.OnPressed += _ => Map.CenterOnPlayer();
        zoomIn.OnPressed += _ => Map.AddRadarRange(-45); zoomOut.OnPressed += _ => Map.AddRadarRange(45);
        pause.OnPressed += _ => Model.Paused = pause.Pressed;
        var sidebar = Column(8); sidebar.Name = "VoyageSidebar"; sidebar.HorizontalExpand = false;
        sidebar.SetWidth = 360; sidebar.MinWidth = 360; sidebar.MaxWidth = 360; sidebar.VerticalExpand = true;
        body.AddChild(sidebar);
        _systemName = Text("", true); sidebar.AddChild(_systemName);
        _systemRole = Text(""); _systemRole.FontColorOverride = Color.FromHex("#9BB0BE"); sidebar.AddChild(_systemRole);
        _details = new TabContainer { HorizontalExpand = true, VerticalExpand = true }; sidebar.AddChild(_details);
        var orderScroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        _orders = Column(8); orderScroll.AddChild(_orders); _details.AddChild(orderScroll); _details.SetTabTitle(0, "Заказы");
        var flightScroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        _flights = Column(8); flightScroll.AddChild(_flights); _details.AddChild(flightScroll); _details.SetTabTitle(1, "Рейсы");
        var crewScroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        _crews = Column(8); crewScroll.AddChild(_crews); _details.AddChild(crewScroll); _details.SetTabTitle(2, "Экипажи");
        sidebar.AddChild(Text("Экипаж рейса"));
        _crewPicker = new OptionButton { HorizontalExpand = true }; sidebar.AddChild(_crewPicker);
        _crewPicker.OnItemSelected += e => { _crewId = e.Id; if (Model.Live) _selectedCrew = Model.CrewIdentifiers.GetValueOrDefault(e.Id); _crewPicker.SelectId(e.Id); RenderOrders(); RenderAdventure(); };
        var hireCrew = new Button { Text = "Нанять экипаж", HorizontalExpand = true };
        hireCrew.OnPressed += _ => _details.CurrentTab = 2; sidebar.AddChild(hireCrew);
        _selection = Text(""); sidebar.AddChild(Card(_selection, "#14343C"));
        _result = Text(""); _result.FontColorOverride = Color.FromHex("#86D9B5");
        _dispatch = new Button { Text = "Показать рейс", HorizontalExpand = true, Disabled = true }; sidebar.AddChild(_dispatch);
        _dispatch.OnPressed += _ =>
        {
            if (Model.Live)
            {
                var mercenary = _crewId is { } id && Model.Crews.Any(c => c.Id == id && c.Offer.Id == "mercenary");
                if (!mercenary && _order != null && Model.System(_selected).Known) { AddDelivery(_order); _details.CurrentTab = 4; return; }
                Send(mercenary ? VoyageAction.Patrol : VoyageAction.Dispatch); return;
            }
            var survey = !Model.System(_selected).Known;
            if (_crewId == null || !Model.Dispatch(_selected, survey, _crewId.Value)) { _result.Text = "Рейс недоступен"; return; }
            _result.Text = survey ? "Разведывательный рейс отправлен" : "Демонстрационный рейс отправлен";
            _details.CurrentTab = 1; _revision = -1;
        };
        sidebar.AddChild(_result);
        InitializeAdventure(sidebar);
        InitializeBaskets();
        var legend = Row();
        foreach (var (name, hex) in new[] { ("Маршрут", "#34636F"), ("Неизучено", "#75899D"),
                     ("Опасный участок", "#D18B61"), ("Мой рейс", "#63DED6"), ("Разведка", "#BCACFF"), ("Чужой рейс", "#9AA5AE") })
        { var label = Text(name); label.FontColorOverride = Color.FromHex(hex); legend.AddChild(label); }
        root.AddChild(legend);
        RenderCrews(); SelectSystem(_selected);
    }
    private static BoxContainer Column(int separation) => new() { Orientation = BoxContainer.LayoutOrientation.Vertical,
        SeparationOverride = separation, HorizontalExpand = true };
    private static BoxContainer Row() => new() { Orientation = BoxContainer.LayoutOrientation.Horizontal,
        SeparationOverride = 10, HorizontalExpand = true };
    private static Label Text(string text, bool heading = false)
    {
        var label = new Label { Text = text, HorizontalExpand = true, ClipText = true, MinWidth = 0 };
        if (heading) label.AddStyleClass(StyleNano.StyleClassLabelHeading);
        return label;
    }
    private static PanelContainer Card(Control contents, string background = "#1B2C38")
    {
        contents.Margin = new Thickness(10);
        return new PanelContainer { HorizontalExpand = true, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(background) }, Children = { contents } };
    }
    private static string Money(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " спессо";

    public void SelectSystem(string id)
    {
        if (_selected != id) _order = null;
        _selected = Map.Selected = id;
        _details.CurrentTab = 0; RenderOrders(); RenderAdventure();
    }
    private void RenderOrders()
    {
        var system = Model.System(_selected);
        _systemName.Text = system.Known ? system.Name : "Неисследованная система";
        _systemRole.Text = system.Known ? system.Role + " · " + system.Orders.Length + " заказа" : system.Name + " · маршрут не изучен";
        _orders.DisposeAllChildren();
        if (!system.Known)
        {
            _orders.AddChild(Card(Text("Данные о спросе отсутствуют")));
            Map.Planned = Model.SurveyPath(_selected);
            _selection.Text = Map.Planned.Length < 2 ? "Нет доступного маршрута" : "Разведка · " + system.Name;
            _selection.ToolTip = _selection.Text;
            _dispatch.Text = "Отправить разведку"; _dispatch.Disabled = Map.Planned.Length < 2 || _crewId == null || Model.Crews.Any(c => c.Id == _crewId && c.Offer.Id == "mercenary"); return;
        }
        foreach (var order in system.Orders)
        {
            var card = Column(7);
            var row = Row();
            var icon = new TradeItemPreview { SetSize = new Vector2(42) }; icon.SetPrototype(order.Prototype); row.AddChild(icon);
            var name = Text(order.Name, true); name.ToolTip = order.Name; row.AddChild(name); card.AddChild(row);
            if (Model.Live)
            {
                var manifest = new RichTextLabel { HorizontalExpand = true, MaxWidth = 330 }; manifest.SetMessage(order.Name); card.AddChild(manifest);
            }
            else card.AddChild(Text("Нужно: " + order.Quantity));
            var reward = Text(Money(order.Reward), true); reward.FontColorOverride = Color.FromHex("#8EE4C0"); card.AddChild(reward);
            var buyer = Text(order.Buyer); buyer.ToolTip = order.Buyer; card.AddChild(buyer);
            var select = new Button { Text = Model.Live ? _deliveryCart.ContainsKey(order.Id) ? "В корзине" : "В корзину доставки" : _order?.Id == order.Id ? "В маршруте" : "Выбрать заказ", HorizontalExpand = true,
                Disabled = Model.Live && (_cartPending || _deliveryCart.ContainsKey(order.Id)) };
            select.OnPressed += _ => { _order = order; if (Model.Live) AddDelivery(order); else RenderOrders(); };
            card.AddChild(select); _orders.AddChild(Card(card, _order?.Id == order.Id ? "#20464B" : "#1B2C38"));
        }
        Map.Planned = Model.Path(VoyagePrototype.Home, _selected);
        var names = string.Join(" → ", Map.Planned.Select(id => Model.System(id).Name));
        var assigned = Model.Crews.FirstOrDefault(c => c.Id == _crewId);
        var minutes = assigned == null ? Model.Minutes(Map.Planned) * (Model.Live ? 2 * Model.TimeScale : 1) : (int)Math.Ceiling(Model.TravelSeconds(Map.Planned, assigned, false) * (Model.Live ? 2 * Model.TimeScale : 1) / 60d);
        _selection.Text = _selected == VoyagePrototype.Home ? "Текущая система" :
            minutes + " мин · " + (_order == null ? system.Orders.Length + " заказа" : Money(_order.Reward));
        _selection.ToolTip = names;
        var mercenary = Model.Crews.Any(c => c.Id == _crewId && c.Offer.Id == "mercenary");
        _dispatch.Text = mercenary ? "Отправить наёмников" : "Отправить рейс";
        _dispatch.Disabled = Map.Planned.Length < 2 || !mercenary && _order == null || _crewId == null;
    }
    private void RenderCrews()
    {
        _crewPicker.Clear();
        var available = Model.Crews.Where(c => !Model.IsBusy(c)).ToArray();
        if (!available.Any(c => c.Id == _crewId)) _crewId = available.FirstOrDefault()?.Id;
        if (available.Length == 0) { _crewPicker.AddItem("Нет свободного экипажа", -1); _crewPicker.SelectId(-1); }
        foreach (var crew in available) _crewPicker.AddItem("№" + crew.Id + " · " + crew.Offer.Name + " · ур. " + crew.Level, crew.Id);
        if (_crewId is { } selected) _crewPicker.SelectId(selected);
        _crewPicker.Disabled = available.Length == 0;
        _crews.DisposeAllChildren();
        _crews.AddChild(Text((Model.Live ? "Банковский счёт: " : "Демо-бюджет: ") + Money(Model.Live ? Model.BankBalance : Model.HiringBudget), true));
        var offers = Model.Live ? Model.HireOffers : VoyagePrototype.CrewOffers.Select(o => new VoyageHireView(o.Id, o.Id, 1, o.Cost)).ToArray();
        foreach (var listing in offers.OrderBy(o => o.Offer).ThenBy(o => o.Level))
        {
            var offer = VoyagePrototype.CrewOffers.Single(o => o.Id == listing.Offer);
            var body = Column(6); body.AddChild(Text(offer.Name, true));
            body.AddChild(Text("Уровень " + listing.Level + " · состав " + offer.Members));
            var hire = new Button { Text = "Нанять · " + Money(listing.Cost), HorizontalExpand = true, Disabled = (Model.Live ? Model.BankBalance : Model.HiringBudget) < listing.Cost };
            hire.OnPressed += _ =>
            {
                if (Model.Live) { Send(VoyageAction.Hire, listing.Id); return; }
                var recruited = Model.Hire(offer.Id); if (recruited == null) return;
                _crewId = recruited.Id; RenderCrews(); RenderOrders(); _result.Text = "Нанят экипаж №" + recruited.Id;
            };
            body.AddChild(hire); _crews.AddChild(Card(body));
        }
        _budgetLabels.Clear();
        foreach (var budget in Model.Budgets)
        {
            var value = Text(budget.Key + ": " + Money(budget.Value)); _crews.AddChild(value); _budgetLabels[budget.Key] = value;
        }
        foreach (var crew in Model.Crews)
            _crews.AddChild(Card(Text("№" + crew.Id + " · " + crew.Offer.Name + " · ур. " + crew.Level + " · опыт " + crew.Experience + "/" + (crew.Level * (crew.Level + 1) * 30) + " · " + (Model.IsBusy(crew) ? "В рейсе" : "Свободен"))));
    }
    private string CrewState() => string.Join(",", Model.Crews.Select(c => c.Id + ":" + Model.IsBusy(c)));
    private string _crewState = "";
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var now = _clock.RealTime;
        if (_lastFrame != TimeSpan.Zero && VisibleInTree) Model.Advance((now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        if (_riskStatus != null) _riskStatus.Text = "Оценка риска: " + (Model.SelectedRisk * 100).ToString("0.0", CultureInfo.InvariantCulture) + "% · цена " + (VoyageGalaxy.PriceFactor(Model.System(_selected).Position) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        if (now < _nextText) return;
        _nextText = now + TimeSpan.FromSeconds(.25);
        _summary.Text = (Model.Live ? Model.Origin + " · Счёт " + Money(Model.BankBalance) + " · " : "") + "Системы " + Model.Systems.Count(s => s.Known) + "/" + Model.Systems.Count + " · Заказы " +
            Model.Systems.Where(s => s.Known).Sum(s => s.Orders.Length) + " · В пути " + Model.Flights.Count(f => f.Progress(Model.Clock) < 1);
        var crewState = CrewState();
        if (_crewState != crewState) { _crewState = crewState; RenderCrews(); RenderOrders(); }
        if (_revision != Model.Revision) { _revision = Model.Revision; RenderOrders(); if (Model.Live) _flightCount = -1; }
        if (_flightCount != Model.Flights.Count)
        {
            _flightCount = Model.Flights.Count; _flights.DisposeAllChildren(); _flightValues.Clear();
            foreach (var flight in Model.Flights.AsEnumerable().Reverse())
            {
                var body = Column(6); body.AddChild(Text(flight.Name, true));
                var route = Text(Model.System(flight.Stops[0]).Name + " → " + Model.System(flight.Stops[^1]).Name); route.ToolTip = route.Text; body.AddChild(route);
                if (Model.Live && Model.ServerFlights[Model.Flights.IndexOf(flight)].Kind == 2 && Model.ServerFlights[Model.Flights.IndexOf(flight)] is { Paid: true, Failed: false, CargoIssued: false } parcel)
                { var claim = new Button { Text = "Получить груз", HorizontalExpand = true }; claim.OnPressed += _ => SendAdventure(VoyageAction.ClaimCargo, operation: parcel.Id); body.AddChild(claim); }
                if (flight.CrewId is { } crewId) body.AddChild(Text("Мой рейс · экипаж №" + crewId));
                else body.AddChild(Text(Model.Live && Model.ServerFlights[Model.Flights.IndexOf(flight)].Kind == 4 ? (flight.IsLocal ? "Моя экспедиционная команда" : "Команда другого игрока") : flight.IsLocal ? "Мой рейс · экипаж потерян" : "Другой перевозчик"));
                var value = Text(""); _flightValues[flight] = value; body.AddChild(value); _flights.AddChild(Card(body));
            }
        }
        foreach (var (flight, label) in _flightValues)
        {
            var progress = flight.Progress(Model.Clock);
            if (Model.Live)
            {
                var index = Model.Flights.IndexOf(flight); var live = Model.ServerFlights[index];
                var leg = live.Failed ? "Провал · экипаж и груз потеряны" : live.Paid ? (live.Kind == 2 ? (live.CargoIssued ? "Груз получен" : "Груз прибыл") : "Экипаж вернулся") : live.Delivered ? "Обратный рейс" : "В пути";
                if (live.Combat >= 0) leg += live.Combat == 0 ? " · без результата" : live.Combat == 1 ? " · пираты подавлены" : " · путь зачищен";
                label.Text = leg + (live.Paid ? "" : " · " + (int)Math.Ceiling(Math.Max(0, live.Return - Model.Clock) / 60) + " мин");
                label.ToolTip = live.Origin + " · " + live.Name; continue;
            }
            label.Text = progress >= 1 ? "Прибыл" : (int)(progress * 100) + "% · " + (int)Math.Ceiling((flight.Departure + flight.Seconds - Model.Clock) / Model.Speed) + " с";
            label.FontColorOverride = progress >= 1 ? Color.FromHex("#87DAB3") : Color.FromHex("#63DED6");
        }
    }
}
