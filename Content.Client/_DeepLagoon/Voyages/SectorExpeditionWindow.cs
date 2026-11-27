using Content.Client._DeepLagoon.Economy;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
namespace Content.Client._DeepLagoon.Voyages;

public sealed partial class SectorExpeditionWindow : DefaultWindow
{
    public NetEntity Console { get; }
    public VoyagePrototype Model { get; } = new();
    public VoyageMapControl Map { get; }
    public event Action<SectorExpeditionRequest>? Request;
    private SectorExpeditionSnapshot? _state;
    private readonly RichTextLabel _departureReason;
    private readonly Label _balance, _status, _selectedTitle, _travel, _progress, _myStats;
    private readonly BoxContainer _orders, _planetCards, _crewCards, _hireCards, _planetMapHost, _researchRequestFields;
    private readonly Button _return, _leave, _publish, _takeOff;
    private readonly Dictionary<int, (Label Distance, Button Land, RichTextLabel Reason)> _planetValues = new();
    private readonly TabContainer _tabs;
    private readonly LineEdit _pool = new() { Text = "30000", HorizontalExpand = true }, _crewPrice = new() { Text = "15000", HorizontalExpand = true },
        _description = new() { PlaceHolder = "Услуги экипажа", HorizontalExpand = true };
    private Guid _order;
    private string _selected = VoyageRouteModel.Home;
    private string _orderKey = "", _crewKey = "", _planetKey = "";
    private int _selectedPlanet = -1;
    private VoyagePrototype? _planetModel;
    private VoyageMapControl? _planetMap;
    private string _planetSector = "";
    public SectorExpeditionWindow(NetEntity console)
    {
        _description.OnTextChanged += _ => { if (_description.Text.Length > 500) _description.Text = _description.Text[..500]; };
        Console = console; Model.UseServer(); Title = "Экспедиции сектора";
        MinSize = new Vector2(1020, 680); SetSize = new Vector2(1240, 790);
        var root = Column(8); Contents.AddChild(root);
        var header = Row(); header.AddChild(Text("ЭКСПЕДИЦИИ / СЕКТОРЫ")); _balance = Text(""); header.AddChild(_balance);
        _takeOff = new Button { Text = "Взлететь с планеты", Disabled = true }; _takeOff.OnPressed += _ => Send(SectorExpeditionAction.LeavePlanet); header.AddChild(_takeOff);
        _return = new Button { Text = "Вернуться домой", Disabled = true }; _return.OnPressed += _ => Send(_state?.ActiveOrder != Guid.Empty ? SectorExpeditionAction.AbandonResearch : SectorExpeditionAction.ReturnHome); header.AddChild(_return); root.AddChild(header);
        _tabs = new TabContainer { HorizontalExpand = true, VerticalExpand = true }; root.AddChild(_tabs);
        var sectorPage = Row(); _tabs.AddChild(sectorPage); _tabs.SetTabTitle(0, "Карта секторов");
        var left = Column(6); left.VerticalExpand = true;
        var tools = Row();
        var center = new Button { Text = "Центр" }; var all = new Button { Text = "Весь сектор" }; var plus = new Button { Text = "+" }; var minus = new Button { Text = "−" };
        tools.AddChild(center); tools.AddChild(all); tools.AddChild(plus); tools.AddChild(minus); left.AddChild(tools);
        Map = new VoyageMapControl(Model); left.AddChild(Map); sectorPage.AddChild(left);
        center.OnPressed += _ => Map.CenterOnPlayer();
        all.OnPressed += _ => Map.ShowSector(); plus.OnPressed += _ => Map.AddRadarRange(-160); minus.OnPressed += _ => Map.AddRadarRange(160);
        Map.SelectedSystem += id => { _selected = id; _order = Guid.Empty; _orderKey = ""; if (_state != null) RenderOrders(_state); Send(SectorExpeditionAction.Refresh); };
        var side = Column(8); side.SetWidth = side.MinWidth = side.MaxWidth = 360; side.HorizontalExpand = false; side.VerticalExpand = true;
        _selectedTitle = Text(""); _progress = Text(""); _travel = Text(""); side.AddChild(_selectedTitle); side.AddChild(_progress); side.AddChild(_travel);
        _departureReason = new RichTextLabel { HorizontalExpand = true, Visible = false, Name = "SectorDepartureBlockReason" };
        side.AddChild(_departureReason);
        _orders = Column(6); side.AddChild(Scroll(_orders));
        _researchRequestFields = Column(6);
        _researchRequestFields.AddChild(Text("Бюджет запроса, спессо")); _researchRequestFields.AddChild(_pool);
        _publish = new Button { Text = "Опубликовать запрос на исследование", HorizontalExpand = true };
        _publish.OnPressed += _ => { if (long.TryParse(_pool.Text, out var amount)) Ask("Резервирование: " + Money(amount), () => Send(SectorExpeditionAction.PostResearch, price: amount)); }; _researchRequestFields.AddChild(_publish); side.AddChild(_researchRequestFields); sectorPage.AddChild(side);
        var current = Row(); _tabs.AddChild(current); _tabs.SetTabTitle(1, "Текущий сектор");
        _planetMapHost = Column(6); _planetMapHost.VerticalExpand = true; current.AddChild(_planetMapHost);
        var planetsSide = Column(6); planetsSide.SetWidth = planetsSide.MinWidth = planetsSide.MaxWidth = 360; planetsSide.HorizontalExpand = false;
        _planetCards = Column(6); planetsSide.AddChild(Scroll(_planetCards));
        _leave = new Button { Text = "Покинуть планету", Disabled = true, HorizontalExpand = true }; _leave.OnPressed += _ => Send(SectorExpeditionAction.LeavePlanet); planetsSide.AddChild(_leave); current.AddChild(planetsSide);
        _crewCards = Column(7); _tabs.AddChild(Scroll(_crewCards)); _tabs.SetTabTitle(2, "Найм экипажей");
        var own = Column(9); _tabs.AddChild(own); _tabs.SetTabTitle(3, "Мой экипаж");
        _myStats = Text("Профиль экипажа ещё не создан"); own.AddChild(_myStats);
        own.AddChild(Text("Цена услуг, спессо")); own.AddChild(_crewPrice); own.AddChild(_description);
        var advertise = new Button { Text = "Опубликовать корабль и экипаж", HorizontalExpand = true };
        advertise.OnPressed += _ => { if (long.TryParse(_crewPrice.Text, out var price)) Send(SectorExpeditionAction.PublishCrew, price: price, text: _description.Text); }; own.AddChild(advertise);
        var withdraw = new Button { Text = "Снять предложение", HorizontalExpand = true }; withdraw.OnPressed += _ => Send(SectorExpeditionAction.WithdrawCrew); own.AddChild(withdraw);
        _hireCards = Column(7); _tabs.AddChild(Scroll(_hireCards)); _tabs.SetTabTitle(4, "Договоры");
        _status = Text("Подключение к консоли..."); root.AddChild(_status);
    }
    private static string Money(long amount) => amount.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " спессо";
    private static BoxContainer Column(int gap) => new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = gap };
    private static BoxContainer Row() => new() { Orientation = BoxContainer.LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 8 };
    private static Label Text(string text) => new() { Text = text, HorizontalExpand = true, ClipText = true, MinWidth = 0 };
    private static ScrollContainer Scroll(Control content) => new() { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false, Children = { content } };
    private static PanelContainer Card(Control content)
    { content.Margin = new Thickness(9); return new() { HorizontalExpand = true, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1B303A") }, Children = { content } }; }
    private void Ask(string text, Action confirm)
    {
        var dialog = new DefaultWindow { Title = "Подтверждение", MinSize = new Vector2(420, 140) };
        var content = Column(10); content.AddChild(Text(text)); var buttons = Row();
        var yes = new Button { Text = "Подтвердить", HorizontalExpand = true }; var no = new Button { Text = "Отмена", HorizontalExpand = true };
        yes.OnPressed += _ => { yes.Disabled = true; confirm(); dialog.Close(); }; no.OnPressed += _ => dialog.Close();
        buttons.AddChild(yes); buttons.AddChild(no); content.AddChild(buttons); dialog.Contents.AddChild(content); dialog.OpenCentered();
    }
    public SectorExpeditionRequest RefreshRequest() => new() { Console = Console, Action = SectorExpeditionAction.Refresh, Sector = _selected };
    private void Send(SectorExpeditionAction action, long price = 0, string text = "", Guid offer = default, Guid order = default, int planet = -1)
        => Request?.Invoke(new() { Console = Console, Action = action, Sector = _selected, Order = order == Guid.Empty ? _order : order,
            Offer = offer, Price = price, Text = text, Planet = planet });
    public void Apply(SectorExpeditionSnapshot snapshot)
    {
        if (snapshot.Status.Length > 0) _status.Text = snapshot.Status;
        if (snapshot.Map.Origin.Length == 0) return;
        if ((_state == null || snapshot.FocusSelected) && snapshot.Selected.Length > 0) _selected = snapshot.Selected;
        var own = snapshot.Crews.FirstOrDefault(c => c.Mine);
        _myStats.Text = own == null ? "Профиль экипажа ещё не создан" : own.Ship + " · уровень " + own.Level + " · опыт " + own.Experience + " · состав " + own.Members;
        if (snapshot.ActiveOrder != Guid.Empty && snapshot.ClaimedSector == _selected) _order = snapshot.ActiveOrder;
        _return.Text = snapshot.ActiveOrder == Guid.Empty ? "Вернуться домой" : snapshot.Home ? "Отказаться от заказа" : snapshot.Progress.GetValueOrDefault(snapshot.ClaimedSector) >= 100 ? "Завершить и вернуться домой" : "Отказаться и вернуться домой";
        _state = snapshot; Model.Apply(snapshot.Map);
        if (!VoyageVisibility.VisibleSystems(Model, snapshot.CurrentSector).Contains(_selected)) _selected = VoyageRouteModel.Home;
        Map.Selected = _selected;
        _balance.Text = Money(snapshot.Map.Bank); _return.Disabled = !snapshot.CanReturn;
        _leave.Disabled = _takeOff.Disabled = !snapshot.OnPlanet || snapshot.InFlight;
        RenderOrders(snapshot); RenderPlanets(snapshot); RenderCrew(snapshot);
        if (snapshot.Status.Length == 0) _status.Text = snapshot.InFlight ? "БСС-перелёт" : snapshot.OnPlanet ? $"Цели экспедиции: {snapshot.MissionDone}/{snapshot.MissionTotal}" : snapshot.Home ? "Родной сектор" : "Сектор: " + Model.System(snapshot.CurrentSector).Name;
    }
    private void RenderOrders(SectorExpeditionSnapshot state)
    {
        var node = Model.System(_selected); _selectedTitle.Text = node.Name; _publish.Disabled = SectorResearchDefinition.Settled(_selected) || state.Progress.GetValueOrDefault(_selected) >= 100; _researchRequestFields.Visible = !_publish.Disabled;
        _progress.Text = "Исследовано: " + state.Progress.GetValueOrDefault(_selected) + "%";
        _travel.Text = state.FlightSeconds > 0 ? "БСС: " + state.FlightSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " с" : "БСС: —";
        var orders = state.Orders.Where(o => o.Sector == _selected).ToArray();
        if (!orders.Any(o => o.Id == _order)) _order = Guid.Empty;
        _travel.Visible = state.CanDepart;
        var reason = state.CanDepart ? "" : state.DepartureBlockReason;
        var warning = state.WithoutFtlDrive && state.CanDepart ? "Без БСС-двигателя: время перелёта ×4." : "";
        var notice = reason.Length > 0 ? reason : warning;
        _departureReason.Visible = notice.Length > 0;
        _departureReason.SetMessage(notice);
        var key = _selected + state.CanDepart + state.DepartureBlockReason + state.InFlight + string.Join(";", orders.Select(o => o.Id + ":" + o.Remaining)) + _order + string.Join(";", state.Map.Products.Select(p => p.Id + ":" + p.Price));
        if (_orderKey == key) return; _orderKey = key; _orders.DisposeAllChildren();
        foreach (var order in orders)
        {
            var card = Column(5); card.AddChild(Text(order.Buyer)); card.AddChild(Text("Пул: " + Money(order.Remaining)));
            card.AddChild(Text("Планеты: " + order.Completed + "/" + order.Planets));
            var choose = new Button { Text = state.ActiveOrder == order.Id ? "Рейс выполняется" : "Взять заказ и вылететь", HorizontalExpand = true,
                Disabled = state.ActiveOrder != Guid.Empty || !state.CanDepart || state.InFlight, ToolTip = state.CanDepart ? null : notice };
            choose.OnPressed += _ => Send(SectorExpeditionAction.AcceptResearch, order: order.Id); card.AddChild(choose);
            if (order.Mine) { var cancel = new Button { Text = "Отменить запрос", HorizontalExpand = true }; cancel.OnPressed += _ => Ask("Вернуть неиспользованный остаток: " + Money(order.Remaining), () => Send(SectorExpeditionAction.CancelResearch, order: order.Id)); card.AddChild(cancel); }
            _orders.AddChild(Card(card));
        }
        if (SectorResearchDefinition.Settled(_selected))
        {
            foreach (var product in state.Map.Products)
            {
                var line = Row(); var icon = new TradeItemPreview { SetSize = new Vector2(36) }; icon.SetPrototype(product.Prototype); line.AddChild(icon);
                var details = Column(4); var title = Text(product.Name); title.ToolTip = product.Name; details.AddChild(title); details.AddChild(Text(Money(product.Price))); line.AddChild(details); _orders.AddChild(Card(line));
            }
        }
        else { if (orders.Length == 0) _orders.AddChild(Text("Открытых запросов нет")); }
        Map.Planned = node.Known ? Model.Path(VoyageRouteModel.Home, _selected) : Model.SurveyPath(_selected);
    }
    private void RenderPlanets(SectorExpeditionSnapshot state)
    {
        _tabs.SetTabTitle(1, state.Home ? "Текущий сектор · дома" : "Текущий сектор");
        var mapKey = state.CurrentSector + string.Join(";", state.Planets.Select(p => p.Index + ":" + p.Name + ":" + p.Type + ":" + p.X + ":" + p.Y));
        if (_planetSector != mapKey)
        {
            _planetMapHost.DisposeAllChildren(); _planetModel = new VoyagePrototype(); _planetModel.Systems.Clear(); _planetModel.Lanes.Clear(); _planetModel.Flights.Clear();
            foreach (var planet in state.Planets) _planetModel.Systems.Add(new("planet-" + planet.Index, planet.Name, new Vector2(planet.X, planet.Y), planet.Type, "Планета", true, []));
            _planetMap = new VoyageMapControl(_planetModel) { OverviewDots = false }; _planetMap.SetView(1750, Vector2.Zero); _planetMap.Selected = ""; _planetMapHost.AddChild(_planetMap);
            _planetMap.SelectedSystem += id => { if (int.TryParse(id[7..], out var index)) { _selectedPlanet = index; _planetKey = ""; RenderPlanets(_state!); } };
            _planetMap.SelectionCleared += () => { _selectedPlanet = -1; _planetKey = ""; RenderPlanets(_state!); };
            _planetMap.LandingRequested += id => { if (int.TryParse(id[7..], out var index)) Send(SectorExpeditionAction.Land, planet: index); };
            _planetSector = mapKey; _planetKey = "";
        }
        if (!state.Planets.Any(p => p.Index == _selectedPlanet)) _selectedPlanet = -1;
        if (_planetModel != null)
            foreach (var planet in state.Planets)
            {
                var index = _planetModel.Systems.FindIndex(s => s.Id == "planet-" + planet.Index);
                if (index >= 0) _planetModel.Systems[index] = _planetModel.Systems[index] with { Researched = planet.Completed };
            }
        if (_planetMap != null)
        {
            _planetMap.Selected = _selectedPlanet >= 0 ? "planet-" + _selectedPlanet : "";
            _planetMap.LandingEnabled = state.Planets.FirstOrDefault(p => p.Index == _selectedPlanet)?.CanLand == true;
            _planetMap.LandingBlockReason = state.Planets.FirstOrDefault(p => p.Index == _selectedPlanet)?.LandingBlockReason ?? "";
            var active = state.Planets.FirstOrDefault(p => p.Index == state.ActivePlanet);
            _planetMap.VesselPosition = state.OnPlanet && active != null ? new Vector2(active.X, active.Y) : state.InFlight ? null : new Vector2(state.ShipX, state.ShipY);
        }
        foreach (var planet in state.Planets)
            if (_planetValues.TryGetValue(planet.Index, out var current))
            {
                current.Distance.Text = ((int)planet.Distance) + " м"; current.Land.Disabled = !planet.CanLand;
                current.Land.ToolTip = planet.CanLand || planet.LandingBlockReason.Length == 0 ? null : planet.LandingBlockReason;
                current.Reason.Visible = _selectedPlanet == planet.Index && !planet.CanLand && planet.LandingBlockReason.Length > 0;
                current.Reason.SetMessage(planet.LandingBlockReason);
            }
        var key = _selectedPlanet + ":" + state.OnPlanet + ":" + state.ActivePlanet + ":" + string.Join(";", state.Orders.Select(o => o.Id + ":" + o.Remaining)) + string.Join(";", state.Planets.Select(p => p.Index + ":" + p.Completed + ":" + p.Occupied));
        if (_planetKey == key) return; _planetKey = key; _planetCards.DisposeAllChildren(); _planetValues.Clear(); _liveMission = null;
        foreach (var planet in state.Planets)
        {
            var card = Column(5);
            var select = new Button { Text = planet.Name, ToggleMode = true, Pressed = _selectedPlanet == planet.Index, HorizontalExpand = true };
            select.OnPressed += _ => { _selectedPlanet = planet.Index; _planetKey = ""; if (_planetMap != null) _planetMap.Selected = "planet-" + planet.Index; RenderPlanets(_state!); }; card.AddChild(select);
            if (planet.Completed)
                card.AddChild(new PanelContainer { Name = "SectorPlanetResearched-" + planet.Index,
                    HorizontalAlignment = HAlignment.Left,
                    PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#173A2B") },
                    Children = { new Label { Text = "[Исследовано]", FontColorOverride = Color.FromHex("#90F3A6"), Margin = new Thickness(6, 2) } } });
            else card.AddChild(Text(!planet.Landable ? "Газовый гигант · посадка недоступна" : planet.Occupied ? "Работает другой экипаж" : "Сложность: " + planet.Difficulty));
            if (_selectedPlanet == planet.Index && planet.Landable) AddMissionDetails(card, planet, state);
            var distance = Text(((int)planet.Distance) + " м"); card.AddChild(distance); var land = new Button { Text = "Посадка", Disabled = !planet.CanLand, HorizontalExpand = true };
            land.Visible = _selectedPlanet == planet.Index && planet.Landable;
            land.ToolTip = planet.CanLand || planet.LandingBlockReason.Length == 0 ? null : planet.LandingBlockReason;
            land.OnPressed += _ => { _selectedPlanet = planet.Index; Send(SectorExpeditionAction.Land, planet: planet.Index); }; card.AddChild(land);
            var reason = new RichTextLabel { Name = "SectorPlanetBlockReason-" + planet.Index, HorizontalExpand = true,
                Visible = _selectedPlanet == planet.Index && !planet.CanLand && planet.LandingBlockReason.Length > 0 };
            reason.SetMessage(planet.LandingBlockReason); card.AddChild(reason);
            _planetValues[planet.Index] = (distance, land, reason); _planetCards.AddChild(Card(card));
        }
    }
    private void RenderCrew(SectorExpeditionSnapshot state)
    {
        var key = string.Join(";", state.Crews.Select(c => c.Id + ":" + c.Price + ":" + c.Available + ":" + c.Level + ":" + c.Experience + ":" + c.Listed)) + string.Join(";", state.Hires.Select(h => h.Id + ":" + h.Status));
        if (key == _crewKey) return; _crewKey = key; _crewCards.DisposeAllChildren(); _hireCards.DisposeAllChildren();
        foreach (var crew in state.Crews)
        {
            var card = Column(6); card.AddChild(Text(crew.Ship + " · " + crew.Captain)); card.AddChild(Text("Уровень " + crew.Level + " · опыт " + crew.Experience + " · экипаж " + crew.Members + " · орудия " + crew.Weapons));
            var description = Text(crew.Description); description.ToolTip = crew.Description; card.AddChild(description); card.AddChild(Text(Money(crew.Price)));
            if (crew.Mine) card.AddChild(Text(crew.Listed ? "Моё предложение" : "Мой экипаж · не опубликован"));
            else
            {
                var hire = new Button { Text = "Нанять экипаж", HorizontalExpand = true, Disabled = !crew.Available };
                hire.OnPressed += _ => Ask("Найм «" + crew.Ship + "»\n" + Money(crew.Price), () => Send(SectorExpeditionAction.HireCrew, price: crew.Price, offer: crew.Id)); card.AddChild(hire);
            }
            _crewCards.AddChild(Card(card));
        }
        string[] statuses = ["Ожидает принятия", "Принят", "Ожидает подтверждения", "Оплачен", "Отменён · деньги возвращены", "Не выполнен · деньги возвращены"];
        foreach (var hire in state.Hires)
        {
            var card = Column(6); card.AddChild(Text(hire.Ship + " · " + Money(hire.Price))); card.AddChild(Text(hire.Employer + " → " + hire.Captain));
            card.AddChild(Text(statuses[Math.Clamp(hire.Status, 0, statuses.Length - 1)]));
            void Action(string text, SectorExpeditionAction action) { var button = new Button { Text = text, HorizontalExpand = true }; button.OnPressed += _ => Send(action, order: hire.Id); card.AddChild(button); }
            if (hire.Provider && hire.Status == 0) { Action("Принять", SectorExpeditionAction.AcceptHire); Action("Отклонить", SectorExpeditionAction.DeclineHire); }
            if (hire.Provider && hire.Status == 1) { Action("Услуга выполнена", SectorExpeditionAction.FinishHire); Action("Отказаться и вернуть оплату", SectorExpeditionAction.DeclineHire); }
            if (hire.Buyer && hire.Status == 0) Action("Отменить найм", SectorExpeditionAction.CancelHire);
            if (hire.Buyer && hire.Status == 2) Action("Подтвердить выполнение и оплатить", SectorExpeditionAction.ConfirmHire);
            _hireCards.AddChild(Card(card));
        }
    }
}
