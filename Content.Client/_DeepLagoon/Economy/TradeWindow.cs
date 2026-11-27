using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Economy;

/// <summary>One state owner for both presentations; changing layout never performs a financial action.</summary>
public sealed partial class TradeWindow : FancyWindow
{
    private const string Interface = "SectorMarket";
    private readonly Action<TradeRequest> _send;
    private readonly IPrototypeManager _prototypes = IoCManager.Resolve<IPrototypeManager>();
    private readonly IConfigurationManager _config = IoCManager.Resolve<IConfigurationManager>();
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private readonly TguiPanel _panel = new(inheritChatAppearance: false, captureKeyboard: true, showWindowControls: false);
    private readonly BoxContainer _native = Column();
    private TradeSnapshot _state = new();
    private TradeOfferView? _confirmation;
    private Guid? _selected;
    private string _section = "catalog", _statusKey = "processing", _localError = "";
    private bool _cards, _syncing, _pending, _awaitingRefresh;
    private int _itemIndex, _requestedPage;
    private TradeRequest? _outgoing;
    private bool _refreshQueued;
    private TimeSpan _nextRequest, _lastSnapshot, _sentAt, _prototypeDue, _refreshSentAt;
    private readonly List<EntityPrototype> _prototypeChoices = new();

    public TradeWindow(Action<TradeRequest> send, bool readOnly = false)
    {
        _send = send;
        _readOnly = readOnly;
        Title = Loc.GetString("dl-trade-title");
        MinSize = new(900, 600); SetSize = new(1100, 760);
        _cards = _config.GetCVar(WebUiCVars.MarketCards);
        BuildNative();
        ContentsContainer.AddChild(_native); ContentsContainer.AddChild(_panel);
        _panel.OnAction += OnWebAction;
        _panel.OnClose += Close;
        _panel.SetNativeFallback(() => { _native.Visible = true; _panel.Visible = false; RenderNative(); });
        _panel.SetTguiRestore(() => { _native.Visible = false; _panel.Visible = true; Publish(); });
        _panel.BindStyle(this, Interface, () => !_panel.UsingNative, tgui =>
        {
            if (tgui) _panel.SwitchToTgui(); else _panel.SwitchToNative();
        });
        _native.Visible = !WindowInterfaceStyle.UseTgui(Interface);
        Publish();
        if (_native.Visible) _panel.SwitchToNative();
    }

    private TradeItemView? Item => _itemIndex >= 0 && _itemIndex < _state.Items.Length ? _state.Items[_itemIndex] : null;
    private TradeOfferView? Selected => _section == "catalog" && GroupLoading ? null : _state.Offers.FirstOrDefault(o => o.Id == _selected);
    private static string L(string key) => Loc.GetString("dl-trade-" + key);
    private static string Money(long value) => Loc.GetString("dl-trade-money", ("amount", value));
    private static string DescriptionPreview(string description)
    {
        var words = description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(10)) + (words.Length > 10 ? "…" : "");
    }
    private bool SupportsBattery(string id) => _prototypes.TryIndex<EntityPrototype>(id, out var prototype) && TradeCatalogue.SupportsBattery(prototype);
    private bool HasBattery(TradeOfferView offer) => offer.BuyOrder ? SupportsBattery(offer.Prototype) :
        offer.HasBattery ?? (offer.Charge > 0 || SupportsBattery(offer.Prototype));
    private bool HasBattery(TradeItemView? item) => item != null && (item.HasBattery ?? (item.Charge > 0 || SupportsBattery(item.Prototype)));

    public void Refresh() => QueueRefresh(_state.Page);
    private void QueueRefresh(int page = 0) { _requestedPage = Math.Max(0, page); _refreshQueued = true; }
    private TradeRequest Query(TradeAction action) => new()
    {
        Action = action, Token = _state.Token, Page = _requestedPage,
        Search = _section == "catalog" ? _search.Text.Trim() : "",
        Filter = _section == "mine" ? TradeFilter.Mine : (TradeFilter)_filter.SelectedId,
        Sort = (TradeSort)_sort.SelectedId,
        Category = _section == "catalog" ? TradeCatalogue.Categories[_category.SelectedId] : "",
        Group = _groupKey, SellerPage = _sellerPage, Offer = _selected ?? Guid.Empty,
    };

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        Poll();
    }
    public void Poll()
    {
        if (!(_embeddedRoot?.VisibleInTree ?? VisibleInTree)) return;
        var now = _timing.RealTime;
        if (_localPendingUntil != TimeSpan.Zero && now >= _localPendingUntil)
        { _localPendingUntil = TimeSpan.Zero; RenderLocalMarket(); Publish(); }
        if (_awaitingRefresh && now - _refreshSentAt > TimeSpan.FromSeconds(10)) _awaitingRefresh = false;
        if (_prototypeDue != TimeSpan.Zero && now >= _prototypeDue) { _prototypeDue = TimeSpan.Zero; SearchPrototypes(); }
        if (_pending && _outgoing == null && now - _sentAt > TimeSpan.FromSeconds(10)) { _pending = false; QueueRefresh(); }
        if (!_pending && now - _lastSnapshot > TimeSpan.FromSeconds(30)) QueueRefresh(_state.Page);
        if (now < _nextRequest) return;
        if (_outgoing is { } request && !_awaitingRefresh)
        {
            _outgoing = null; request.Token = _state.Token; _sentAt = now;
            _nextRequest = now + TimeSpan.FromSeconds(.65); _send(request);
        }
        else if (_refreshQueued && !_pending && !_awaitingRefresh)
        {
            _refreshQueued = false; _nextRequest = now + TimeSpan.FromSeconds(.65);
            _lastSnapshot = now; _awaitingRefresh = true; _refreshSentAt = now; _send(Query(TradeAction.Refresh));
        }
    }

    private void ChangeSection(string section, bool refresh = true)
    {
        if (_readOnly && section != "catalog") return;
        if (section is not ("catalog" or "create" or "mine" or "delivery" or "local" or "basket") || section == "local" && _localSend == null) return;
        _section = section; _confirmation = null; _localError = "";
        RenderNative(); Publish();
        if (refresh && section != "local") QueueRefresh();
    }
    private void SetCards(bool cards)
    {
        _cards = cards; _config.SetCVar(WebUiCVars.MarketCards, cards); _config.SaveToFile();
        RenderNative(); Publish();
    }
    private void Select(Guid id)
    {
        if (!_state.Offers.Any(o => o.Id == id)) return;
        _selected = id; _confirmation = null; _localError = "";
        _lotAmount = Selected!.StackUnits.ToString();
        var offer = Selected!;
        if (offer.BuyOrder && !Matches(Item, offer))
        {
            var match = Array.FindIndex(_state.Items, item => Matches(item, offer));
            if (match >= 0) _itemIndex = match;
        }
        QueueRefresh(_state.Page); RenderNativeDetails(); Publish();
    }
    private static bool Matches(TradeItemView? item, TradeOfferView offer) => item != null && item.Prototype == offer.Prototype &&
        item.StackUnits >= offer.StackUnits && item.Condition >= offer.Condition && item.Charge >= offer.Charge;

    private void Prepare(Guid id)
    {
        if (_pending || !_state.Offers.Any(o => o.Id == id)) return;
        Select(id);
        if (Selected is not { } offer) return;
        _confirmation = offer; RenderNativeDetails(); Publish();
    }
    private void Confirm()
    {
        if (_pending || _confirmation is not { } offer || !_state.Offers.Contains(offer)) return;
        if (!offer.Mine && offer.BuyOrder && !Matches(Item, offer)) { Error("status-item_mismatch"); return; }
        if (!offer.Mine && !offer.BuyOrder && offer.Price > _state.Bank) { Error("status-insufficient_spessos"); return; }
        var request = Query(offer.Mine ? TradeAction.Cancel : TradeAction.Deal); request.Offer = offer.Id;
        if (!offer.Mine && offer.BuyOrder) request.Item = Item!.Entity;
        _confirmation = null; SendMutation(request);
    }
    private void Claim(Guid id)
    {
        if (!_state.Deliveries.Any(d => d.Id == id)) return;
        var request = Query(TradeAction.Claim); request.Offer = id; SendMutation(request);
    }
    private void SendMutation(TradeRequest request)
    {
        if (_pending || _state.Token == Guid.Empty || _readOnly && request.Action != TradeAction.Comment) return;
        _pending = true; _outgoing = request; _refreshQueued = false; _localError = "";
        _statusKey = "processing"; RenderNative(); Publish();
    }
    private void Error(string key) { _localError = L(key); RenderNativeDetails(); UpdateDraft(); Publish(); }

    public void Update(TradeSnapshot state)
    {
        if (state.Status == "round_closed")
        {
            _cart.Clear(); _basketQuote = null;
            _outgoing = null; _refreshQueued = false; _confirmation = null;
            _state = new TradeSnapshot { Console = state.Console, Bank = _state.Bank };
        }
        _statusKey = string.IsNullOrEmpty(state.Status) ? "ready" : state.Status;
        _awaitingRefresh = false;
        _pending = _outgoing != null || state.Token == Guid.Empty && state.Status is "processing" or "round_closed";
        if (state.Token != Guid.Empty)
        {
            state.Offers = state.Offers.Select(o => o with { Name = TradeText.Plain(o.Name), Character = TradeText.Plain(o.Character), Description = TradeText.Plain(o.Description) }).ToArray();
            state.Groups = state.Groups.Select(g => g with { Name = TradeText.Plain(g.Name) }).ToArray();
            state.Items = state.Items.Select(i => i with { Name = TradeText.Plain(i.Name) }).ToArray();
            state.Deliveries = state.Deliveries.Select(d => d with { Name = TradeText.Plain(d.Name) }).ToArray();
            if (state.Quote is { } raw) state.Quote = raw with { Lines = raw.Lines.Select(l => l with { Name = TradeText.Plain(l.Name), Character = TradeText.Plain(l.Character) }).ToArray() };
            _basketQuote = state.Quote;
            if (_basketQuote != null) _section = "basket";
            if (state.BasketPurchased) _cart.Clear();
            _basketSubmitting = false;
            _groupKey = state.Group; _sellerPage = state.SellerPage;
            var previous = Item?.Entity;
            _state = state; _lastSnapshot = _timing.RealTime;
            _itemIndex = Math.Max(0, Array.FindIndex(state.Items, item => item.Entity == previous));
            if (!state.Offers.Any(o => o.Id == _selected)) { _selected = state.Offers.FirstOrDefault()?.Id; _lotAmount = Selected?.StackUnits.ToString() ?? "1"; }
            if (_confirmation != null && !state.Offers.Contains(_confirmation)) _confirmation = null;
        }
        else if (!_pending && state.Status != "processing") { _basketQuote = null; _basketSubmitting = false; }
        _localError = ""; SyncChoices(); RenderNative(); Publish();
    }

    private void SearchPrototypes()
    {
        var previous = _prototypeChoices.ElementAtOrDefault(_wanted.SelectedId)?.ID;
        _prototypeChoices.Clear(); _wanted.Clear();
        var text = _prototypeSearch.Text.Trim();
        if (text.Length >= 2)
            _prototypeChoices.AddRange(_prototypes.EnumeratePrototypes<EntityPrototype>()
                .Where(p => !p.Abstract && p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Name).ThenBy(p => p.ID).Take(80));
        for (var i = 0; i < _prototypeChoices.Count; i++) _wanted.AddItem(_prototypeChoices[i].Name, i);
        if (_prototypeChoices.Count > 0) _wanted.SelectId(Math.Max(0, _prototypeChoices.FindIndex(p => p.ID == previous)));
        UpdateDraft(); Publish();
    }
    private void SchedulePrototypeSearch()
    {
        _prototypeChoices.Clear(); _wanted.Clear();
        _prototypeDue = _timing.RealTime + TimeSpan.FromSeconds(.25);
        UpdateDraft(); Publish();
    }
    private string ValidateDraft(out long price, out int quantity, out int units)
    {
        quantity = units = 1;
        if (!long.TryParse(_price.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out price) || price is < 1 or > 1000000000 || _description.Text.Length > 500)
            return L("status-invalid_offer");
        if (_mode.SelectedId == 0) return Item == null ? L("status-supply_item") : "";
        if (_prototypeChoices.ElementAtOrDefault(_wanted.SelectedId) == null) return L("select-prototype");
        if (!int.TryParse(_quantity.Text, out quantity) || quantity is < 1 or > 100 || !int.TryParse(_units.Text, out units) || units < 1)
            return L("status-invalid_offer");
        return price * quantity > _state.Bank ? L("status-insufficient_spessos") : "";
    }
    private void Submit()
    {
        if (_pending) return;
        var error = ValidateDraft(out var price, out var quantity, out var units);
        if (error.Length > 0) { _localError = error; UpdateDraft(); Publish(); return; }
        var buy = _mode.SelectedId == 1;
        ChangeSection("mine", false); _requestedPage = 0;
        var request = Query(buy ? TradeAction.BuyOrder : TradeAction.Sell);
        request.Price = price; request.Quantity = quantity; request.StackUnits = units;
        request.Condition = _condition.SelectedId;
        request.Charge = buy && SupportsBattery(_prototypeChoices[_wanted.SelectedId].ID) ? _charge.SelectedId : 0;
        request.Description = _description.Text;
        if (buy) request.Prototype = _prototypeChoices[_wanted.SelectedId].ID; else request.Item = Item!.Entity;
        SendMutation(request);
    }
}
