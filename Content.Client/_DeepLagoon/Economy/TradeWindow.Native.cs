using System.Linq;
using Content.Shared._DeepLagoon.Economy;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class TradeWindow
{
    private readonly Label _bank = new(), _pages = new();
    private readonly RichTextLabel _status = new(), _validation = new(), _draftName = new(), _draftValue = new();
    private readonly LineEdit _search = new(), _prototypeSearch = new(), _price = new() { Text = "1000" },
        _quantity = new() { Text = "1" }, _units = new() { Text = "1" }, _description = new();
    private readonly OptionButton _filter = new(), _category = new(), _sort = new(), _items = new(), _wanted = new(),
        _mode = new(), _condition = new(), _charge = new();
    private readonly TabContainer _tabs = new() { HorizontalExpand = true, VerticalExpand = true };
    private readonly BoxContainer _detail = Column(), _own = Column(), _deliveries = Column(), _buyFields = Column(), _saleFields = Column(), _chargeField = Column();
    private readonly GridContainer _grid = new() { Columns = 2, HorizontalExpand = true };
    private readonly BoxContainer _catalogPane = Column(), _offersPane = Column();
    private readonly RichTextLabel _offersTitle = new() { HorizontalExpand = true };
    private ScrollContainer _detailPane = default!;
    private readonly Dictionary<Guid, Button> _offerButtons = new();
    private readonly TradeItemPreview _draftIcon = new() { SetSize = new(80, 80) };
    private Button _cardsButton = default!, _listButton = default!, _publish = default!, _prev = default!, _next = default!;
    private static readonly string[] Sections = ["catalog", "create", "mine", "delivery", "local", "basket"];
    private static BoxContainer Column() => new() { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 6 };
    private static BoxContainer Row() => new() { HorizontalExpand = true, SeparationOverride = 6 };
    private static Button AddButton(BoxContainer parent, string text, Action action)
    {
        var button = new Button { Text = text }; button.OnPressed += _ => action(); parent.AddChild(button); return button;
    }
    private static void Text(BoxContainer parent, string text) { var label = new RichTextLabel { Text = text, HorizontalExpand = true }; parent.AddChild(label); }
    private static void Field(BoxContainer parent, string label, Control control) { Text(parent, label); parent.AddChild(control); }
    private static ScrollContainer Scrolling(Control content)
    {
        // These panes scroll vertically. An unbounded horizontal measure lets text
        // widen the details behind clipping and moves the centered preview outside.
        var scroll = new ScrollContainer { HorizontalExpand = true, VerticalExpand = true, HScrollEnabled = false };
        scroll.AddChild(content); return scroll;
    }

    private void BuildNative()
    {
        _native.AddChild(_bank); _native.AddChild(_status);
        var search = Row(); _native.AddChild(search); _search.HorizontalExpand = true; _search.PlaceHolder = L("search"); search.AddChild(_search);
        AddButton(search, L("search-button"), () => Browse());
        AddButton(search, L("refresh"), Refresh);
        _cardsButton = AddButton(search, L("cards"), () => SetCards(true));
        _listButton = AddButton(search, L("list"), () => SetCards(false));
        _cardsButton.ToggleMode = _listButton.ToggleMode = true;
        _search.OnTextEntered += _ => Browse();
        var filters = Row(); _native.AddChild(filters);
        foreach (var (key, id) in new[] { ("all", 0), ("selling-short", 1), ("wanted-short", 2) }) _filter.AddItem(L(key), id);
        for (var i = 0; i < TradeCatalogue.Categories.Length; i++) _category.AddItem(L("category-" + (TradeCatalogue.Categories[i].Length == 0 ? "all" : TradeCatalogue.Categories[i])), i);
        foreach (var (key, id) in new[] { ("sort-new", 0), ("sort-cheap", 1), ("sort-expensive", 2) }) _sort.AddItem(L(key), id);
        foreach (var option in new[] { _filter, _category, _sort })
        { option.HorizontalExpand = true; option.SelectId(0); filters.AddChild(option); option.OnItemSelected += args => { option.SelectId(args.Id); if (!_syncing) Browse(); }; }
        _native.AddChild(_tabs);
        var board = Row(); board.Name = L("board"); _tabs.AddChild(board);
        var left = Column(); left.VerticalExpand = true; board.AddChild(left);
        _catalogPane.VerticalExpand = _offersPane.VerticalExpand = true;
        left.AddChild(_catalogPane); left.AddChild(_offersPane);
        _catalogPane.AddChild(Scrolling(_grid));
        var pages = Row(); _catalogPane.AddChild(pages);
        _prev = AddButton(pages, L("prev"), () => Browse(_state.Page - 1)); pages.AddChild(_pages);
        _next = AddButton(pages, L("next"), () => Browse(_state.Page + 1));
        var heading = Row(); heading.AddChild(_offersTitle); _offersPane.AddChild(heading);
        AddButton(heading, "×", CloseGroup).ToolTip = L("close-offers");
        _offersPane.AddChild(Scrolling(_sellerList));
        _offersPane.AddChild(_sellerPagesBar);
        _detailPane = Scrolling(_detail); _detailPane.HorizontalExpand = false; _detailPane.SetWidth = 280; board.AddChild(_detailPane);
        BuildForm();
        var mine = Scrolling(_own); mine.Name = L("mine"); _tabs.AddChild(mine);
        var delivery = Scrolling(_deliveries); delivery.Name = L("deliveries"); _tabs.AddChild(delivery);
        BuildLocalMarket();
        var basket = Scrolling(_basketBody); basket.Name = L("basket"); _tabs.AddChild(basket);
        _tabs.OnTabChanged += index => { if (!_syncing) ChangeSection(Sections[index]); };
        RenderNative();
    }

    private TradeItemPreview Icon(string prototype, int size = 56)
    {
        var icon = new TradeItemPreview { SetSize = new(size, size), Name = "TradeItemPreview" };
        SetIcon(icon, prototype); return icon;
    }
    private void SetIcon(TradeItemPreview icon, string prototype)
    {
        icon.SetPrototype(prototype);
    }
    private void SyncChoices()
    {
        _syncing = true; _items.Clear();
        for (var i = 0; i < _state.Items.Length; i++)
            _items.AddItem($"{_state.Items[i].Name} ×{_state.Items[i].StackUnits}", i);
        if (_state.Items.Length > 0) _items.SelectId(_itemIndex);
        _items.Disabled = _state.Items.Length == 0; _syncing = false;
    }
    private void RenderNative()
    {
        if (!_native.Visible) return;
        if (_readOnly) for (var i = 1; i < Sections.Length; i++) _tabs.SetTabVisible(i, false);
        _syncing = true; _tabs.CurrentTab = Array.IndexOf(Sections, _section); _syncing = false;
        _bank.Text = Loc.GetString("dl-trade-balance", ("amount", _state.Bank));
        if (_section == "local" && _localState != null) _bank.Text = Loc.GetString("dl-trade-balance", ("amount", _localState.Balance));
        _status.Visible = _section != "local" && _statusKey != "ready"; _status.Text = _localError.Length > 0 ? _localError : L("status-" + _statusKey);
        _search.Parent!.Visible = _section == "catalog";
        _filter.Parent!.Visible = _section == "catalog";
        _cardsButton.Pressed = _cards; _listButton.Pressed = !_cards;
        _grid.Columns = _cards ? Math.Max(2, (int)((_embeddedRoot?.Width ?? Width) - 50) / 230) : 1;
        if (_section == "catalog")
        {
            _catalogPane.Visible = !GroupExpanded; _offersPane.Visible = _detailPane.Visible = GroupExpanded;
            _offersTitle.Text = _state.Groups.FirstOrDefault(g => g.Key == _groupKey)?.Name ?? L("offers");
            _grid.RemoveAllChildren(); _offerButtons.Clear();
            _pages.Text = $"{_state.Page + 1}/{Math.Max(1, _state.Pages)}";
            _prev.Disabled = _pending || _state.Page <= 0; _next.Disabled = _pending || _state.Page + 1 >= _state.Pages;
            foreach (var group in _state.Groups)
            {
                var button = new Button { HorizontalExpand = true, ToggleMode = true, Pressed = group.Key == _groupKey };
                var body = Column(); body.Margin = new(5); button.AddChild(body); body.AddChild(Icon(group.Prototype, _cards ? 56 : 32));
                Text(body, group.Name); Text(body, L(group.BuyOrder ? "wanted-short" : "selling-short"));
                Text(body, L("from") + " " + UnitPrice(group.Price, group.PriceUnits));
                button.OnPressed += _ => SelectGroup(group.Key); _grid.AddChild(button);
            }
            if (_state.Groups.Length == 0 && _state.Offers.Length == 0) _grid.AddChild(new RichTextLabel { Text = L("empty") });
            _sellerList.RemoveAllChildren();
            foreach (var offer in GroupLoading ? [] : _state.Offers)
            {
                var button = new Button { HorizontalExpand = true, ToggleMode = true, Pressed = offer.Id == _selected, Name = "TradeOffer" };
                var body = Row(); body.Margin = new(6); button.AddChild(body); body.AddChild(Icon(offer.Prototype, 40));
                var words = Column(); body.AddChild(words);
                Text(words, offer.Name);
                Text(words, offer.Character);
                var preview = DescriptionPreview(offer.Description);
                if (preview.Length > 0) Text(words, preview);
                Text(words, Money(offer.Price));
                Text(words, L(offer.BuyOrder ? "per-delivery" : "per-lot"));
                Text(words, Loc.GetString("dl-trade-short-state", ("units", offer.StackUnits), ("condition", offer.Condition)));
                if (TradeCatalogue.Commodity(offer.Prototype) != null) Text(words, UnitPrice(offer.Price, offer.StackUnits) + " / " + L("local-unit"));
                button.OnPressed += _ => Select(offer.Id); _offerButtons[offer.Id] = button; _sellerList.AddChild(button);
            }
            if (_state.Groups.Length == 0 && _state.Offers.Length == 0) Text(_sellerList, L("empty"));
            _sellerPagesBar.RemoveAllChildren(); var sellerPages = _sellerPagesBar;
            AddButton(sellerPages, L("prev"), () => SellerPage(_sellerPage - 1)).Disabled = _pending || _sellerPage == 0;
            Text(sellerPages, $"{_sellerPage + 1}/{Math.Max(1, _state.SellerPages)}");
            AddButton(sellerPages, L("next"), () => SellerPage(_sellerPage + 1)).Disabled = _pending || _sellerPage + 1 >= _state.SellerPages;
            RenderNativeDetails();
        }
        if (_section == "mine") RenderMine();
        if (_section == "basket") RenderBasket();
        if (_section == "local") RenderLocalMarket();
        if (_section == "delivery")
        {
            _deliveries.RemoveAllChildren();
            foreach (var delivery in _state.Deliveries)
            {
                var row = Row(); row.AddChild(Icon(delivery.Prototype, 40)); Text(row, delivery.Name);
                AddButton(row, L("claim"), () => Claim(delivery.Id)).Disabled = _pending;
                _deliveries.AddChild(row);
            }
            if (_state.Deliveries.Length == 0) Text(_deliveries, L("empty-deliveries"));
        }
        UpdateDraft();
    }
    private void RenderMine()
    {
        _own.RemoveAllChildren();
        foreach (var offer in _state.Offers.Where(o => o.Mine))
        {
            var row = Column(); var header = Row(); row.AddChild(header); header.AddChild(Icon(offer.Prototype, 48));
            var words = Column(); header.AddChild(words); Text(words, offer.Name); Text(words, Money(offer.Price));
            Text(words, L(offer.BuyOrder ? "wanted-short" : "selling-short"));
            if (_confirmation?.Id == offer.Id) Confirmation(row, offer);
            else AddButton(row, L("cancel"), () => Prepare(offer.Id)).Disabled = _pending;
            _own.AddChild(row);
        }
        if (!_state.Offers.Any(o => o.Mine)) Text(_own, L("empty-mine"));
        var pages = Row(); _own.AddChild(pages);
        AddButton(pages, L("prev"), () => QueueRefresh(_state.Page - 1)).Disabled = _pending || _state.Page == 0;
        Text(pages, $"{_state.Page + 1}/{Math.Max(1, _state.Pages)}");
        AddButton(pages, L("next"), () => QueueRefresh(_state.Page + 1)).Disabled = _pending || _state.Page + 1 >= _state.Pages;
    }
    private void RenderNativeDetails()
    {
        if (!_native.Visible) return;
        if (_section == "catalog" && !GroupExpanded) return;
        foreach (var (id, button) in _offerButtons) button.Pressed = id == _selected;
        _detail.RemoveAllChildren();
        if (Selected is not { } offer) { Text(_detail, L("select-offer")); return; }
        _detail.AddChild(Icon(offer.Prototype, 96)); Text(_detail, offer.Name); Text(_detail, Money(offer.Price));
        Text(_detail, L(offer.BuyOrder ? "per-delivery" : "per-lot"));
        Text(_detail, offer.Character);
        var stateKey = offer.BuyOrder ? "dl-trade-criteria" : "dl-trade-item-state";
        if (!HasBattery(offer)) stateKey += "-no-battery";
        Text(_detail, Loc.GetString(stateKey,
            ("condition", offer.Condition), ("charge", offer.Charge), ("units", offer.StackUnits)));
        Text(_detail, offer.Description);
        RenderComments(_detail, offer);
        if (_readOnly) return;
        if (offer.BuyOrder && !offer.Mine)
        {
            var supply = new OptionButton { HorizontalExpand = true, Name = "TradeSupplyItem" };
            for (var i = 0; i < _state.Items.Length; i++) supply.AddItem($"{_state.Items[i].Name} ×{_state.Items[i].StackUnits}", i);
            if (_state.Items.Length > 0) supply.SelectId(_itemIndex);
            supply.Disabled = _state.Items.Length == 0;
            supply.OnItemSelected += args => { _itemIndex = args.Id; RenderNativeDetails(); Publish(); };
            Field(_detail, L("pallet"), supply);
            if (!Matches(Item, offer)) Text(_detail, L("status-item_mismatch"));
        }
        if (_localError.Length > 0) Text(_detail, _localError);
        if (_confirmation?.Id == offer.Id) Confirmation(_detail, offer);
        else AddButton(_detail, L(offer.Mine ? "cancel" : offer.BuyOrder ? "supply" : offer.Splittable ? "buy-whole" : "buy"), () => Prepare(offer.Id)).Disabled = _pending || _state.Token == Guid.Empty;
        if (!offer.Mine && !offer.BuyOrder)
        {
            if (offer.Splittable)
            {
                var amount = new LineEdit { Text = _lotAmount }; amount.OnTextChanged += _ => { _lotAmount = amount.Text; Publish(); };
                Field(_detail, L("count"), amount);
            }
            AddButton(_detail, L("basket-add"), AddToBasket).Disabled = _pending || _state.Token == Guid.Empty;
        }
        var group = _state.Groups.FirstOrDefault(g => g.Key == _groupKey);
        if (group is { BuyOrder: false } && TradeCatalogue.Commodity(group.Prototype) != null)
        {
            var amount = new LineEdit { Text = _autoAmount }; amount.OnTextChanged += _ => { _autoAmount = amount.Text; Publish(); };
            Field(_detail, L("basket-auto-units"), amount);
            AddButton(_detail, L("basket-auto"), () => QuoteBasket(true)).Disabled = _pending || _state.Token == Guid.Empty;
        }
        if (_section == "mine") RenderMine();
    }
    private void Confirmation(BoxContainer target, TradeOfferView offer)
    {
        Text(target, L(offer.Mine ? "confirm-cancel" : offer.BuyOrder ? "confirm-supply" : "confirm-buy"));
        if (!offer.Mine) Text(target, Money(offer.Price));
        AddButton(target, L("confirm"), Confirm).Disabled = _pending || offer.BuyOrder && !offer.Mine && !Matches(Item, offer);
        AddButton(target, L("back"), () => { _confirmation = null; RenderNativeDetails(); Publish(); });
    }

    private void BuildForm()
    {
        var form = Column(); var scroll = Scrolling(form); scroll.Name = L("create"); _tabs.AddChild(scroll);
        _mode.AddItem(L("create-sale"), 0); _mode.AddItem(L("create-buy"), 1); _mode.SelectId(0); form.AddChild(_mode);
        _mode.OnItemSelected += args => { _mode.SelectId(args.Id); UpdateDraft(); Publish(); };
        var preview = Row(); preview.AddChild(_draftIcon); var summary = Column(); summary.AddChild(_draftName); summary.AddChild(_draftValue); preview.AddChild(summary); form.AddChild(preview);
        Field(_saleFields, L("pallet"), _items); form.AddChild(_saleFields);
        _items.OnItemSelected += args => { _items.SelectId(args.Id); _itemIndex = args.Id; UpdateDraft(); Publish(); };
        Field(_buyFields, L("wanted"), _prototypeSearch); _buyFields.AddChild(_wanted);
        Field(_buyFields, L("quantity"), _quantity); Field(_buyFields, L("stack"), _units);
        Field(_buyFields, L("condition"), _condition); Field(_chargeField, L("charge"), _charge); _buyFields.AddChild(_chargeField); form.AddChild(_buyFields);
        foreach (var number in new[] { 0, 50, 80, 100 }) _condition.AddItem(number + "%", number); _condition.SelectId(100);
        foreach (var number in new[] { 0, 25, 50, 100 }) _charge.AddItem(number + "%", number); _charge.SelectId(0);
        foreach (var option in new[] { _condition, _charge, _wanted }) option.OnItemSelected += args => { option.SelectId(args.Id); UpdateDraft(); Publish(); };
        _prototypeSearch.OnTextChanged += _ => SchedulePrototypeSearch();
        Field(form, L("price"), _price); Field(form, L("description"), _description); form.AddChild(_validation);
        foreach (var input in new[] { _price, _quantity, _units, _description }) input.OnTextChanged += _ => { _localError = ""; UpdateDraft(); Publish(); };
        _publish = AddButton(form, L("sell"), Submit); UpdateDraft();
    }
    private void UpdateDraft()
    {
        if (_publish == null) return;
        var buy = _mode.SelectedId == 1; _buyFields.Visible = buy; _saleFields.Visible = !buy;
        var prototype = buy ? _prototypeChoices.ElementAtOrDefault(_wanted.SelectedId) : null;
        _chargeField.Visible = buy && prototype != null && TradeCatalogue.SupportsBattery(prototype);
        _draftName.Text = buy ? prototype?.Name ?? L("select-prototype") : Item?.Name ?? L("status-supply_item");
        SetIcon(_draftIcon, buy ? prototype?.ID ?? "" : Item?.Prototype ?? "");
        var error = ValidateDraft(out var price, out var quantity, out _);
        _draftValue.Text = Loc.GetString(buy ? "dl-trade-reserve" : "dl-trade-total-price", ("amount", price * quantity));
        _validation.Text = _localError.Length > 0 ? _localError : error;
        _publish.Text = L(buy ? "publish-order" : "sell");
        _publish.Disabled = _pending || _state.Token == Guid.Empty || error.Length > 0;
    }
}
