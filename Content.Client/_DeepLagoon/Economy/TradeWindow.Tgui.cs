using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Economy;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class TradeWindow
{
    private static readonly string[] Labels =
    [
        "comments", "comment-send", "comment-id", "title", "board", "create", "mine", "deliveries", "cards", "list", "search", "refresh", "all",
        "selling-short", "wanted-short", "sort-new", "sort-cheap", "sort-expensive", "category-all", "category-materials",
        "category-equipment", "category-weapons", "category-food", "category-clothing", "category-other", "per-lot", "per-delivery",
        "select-offer", "empty", "empty-mine", "empty-deliveries", "condition", "charge", "stack", "quantity", "description",
        "create-sale", "create-buy", "price", "pallet", "wanted", "select-prototype", "publish-order", "sell", "buy", "supply", "cancel", "claim",
        "confirm", "back", "confirm-buy", "confirm-supply", "confirm-cancel", "seller", "buyer", "count", "state", "battery",
        "remaining", "type-preview", "reserved", "draft-preview", "balance-label", "total-label", "full-item", "no-image", "prev", "next",
        "local", "local-all", "local-unit", "local-stock", "local-empty", "local-cart", "local-remove", "local-fee", "local-confirm", "local-checkout",
        "basket", "basket-add", "basket-quote", "basket-empty", "basket-confirm", "basket-auto", "basket-auto-units", "offers", "from", "buy-whole", "close-offers",
    ];

    private void Publish()
    {
        var labels = new TguiData().String("marketplace", Loc.GetString("dl-marketplace-program-name")); foreach (var key in Labels) labels.String(key, L(key));
        var icons = new TguiData();
        foreach (var id in _state.Offers.Select(o => o.Prototype).Concat(_state.Groups.Select(g => g.Prototype))
                     .Concat(_cart.Values.Select(e => e.Offer.Prototype)).Concat((_basketQuote?.Lines ?? []).Select(l => l.Prototype)).Concat(_state.Items.Select(i => i.Prototype))
                     .Concat(_state.Deliveries.Select(d => d.Prototype)).Concat(_prototypeChoices.Select(p => p.ID))
                     .Concat(LocalRows.Select(i => i.Prototype.Id)).Concat((_localState?.CartDataList ?? []).Select(i => i.Prototype.Id)).Where(id => id.Length > 0).Distinct())
            if (_prototypes.HasIndex<EntityPrototype>(id)) icons.Array(id, TguiSpriteImages.Shared.Item(id));
        _panel.SetStaticData(new TguiData().Object("labels", labels).Object("icons", icons).ToString());
        var error = ValidateDraft(out var price, out var quantity, out _);
        var data = new TguiData().String("section", _section).Bool("cards", _cards).Bool("busy", _pending)
            .Bool("readOnly", _readOnly).Bool("canComment", _state.CanComment).String("commentAuthor", _state.CommentAuthor)
            .String("commentText", _commentText).String("commentOffer", _state.CommentOffer.ToString())
            .Array("comments", _state.Comments.Select(c => new TguiData().String("author", c.Author).String("text", c.Text).String("time", CommentTime(c.Time))))
            .Bool("hasError", _localError.Length > 0)
            .Bool("ready", _state.Token != Guid.Empty && _statusKey != "disabled").String("statusKey", _statusKey)
            .String("status", _localError.Length > 0 ? _localError : L("status-" + _statusKey))
            .String("bank", Money(_section == "local" ? _localState?.Balance ?? 0 : _state.Bank)).Object("local", LocalData())
            .Number("page", _state.Page).Number("pages", Math.Max(1, _state.Pages))
            .Number("total", Math.Max(_state.Total, _state.Offers.Length)).String("search", _search.Text)
            .Number("filter", _filter.SelectedId).Number("category", _category.SelectedId).Number("sort", _sort.SelectedId)
            .String("selected", _selected?.ToString() ?? "").String("item", Item?.Entity.ToString() ?? "")
            .String("confirm", _confirmation?.Id.ToString() ?? "")
            .Bool("groupOpen", GroupExpanded)
            .Array("offers", (GroupLoading ? [] : _state.Offers).Select(OfferData))
            .String("group", _groupKey).Number("sellerPage", _sellerPage).Number("sellerPages", Math.Max(1, _state.SellerPages))
            .String("lotAmount", _lotAmount).String("autoAmount", _autoAmount)
            .Array("groups", _state.Groups.Select(g => new TguiData().String("key", g.Key).String("prototype", g.Prototype).String("name", g.Name)
                .Bool("buy", g.BuyOrder).String("money", UnitPrice(g.Price, g.PriceUnits)).Bool("material", TradeCatalogue.Commodity(g.Prototype) != null)))
            .Array("basket", _cart.Values.Select(e => new TguiData().String("id", e.Offer.Id.ToString()).String("prototype", e.Offer.Prototype)
                .String("name", e.Offer.Name).String("character", e.Offer.Character).Number("units", e.Units).String("money", Money(EntryCost(e.Offer, e.Units)))))
            .Object("quote", QuoteData())
            .Array("items", _state.Items.Select(i => new TguiData().String("id", i.Entity.ToString()).String("name", i.Name).String("prototype", i.Prototype)
                .Number("units", i.StackUnits).Number("condition", i.Condition).Number("charge", i.Charge).Bool("hasBattery", HasBattery(i))))
            .Array("deliveries", _state.Deliveries.Select(d => new TguiData().String("id", d.Id.ToString()).String("name", d.Name).String("prototype", d.Prototype)))
            .Object("draft", new TguiData().Bool("buy", _mode.SelectedId == 1).String("search", _prototypeSearch.Text)
                .String("prototype", _prototypeChoices.ElementAtOrDefault(_wanted.SelectedId)?.ID ?? "")
                .Array("prototypes", _prototypeChoices.Select(p => new TguiData().String("id", p.ID).String("name", p.Name)))
                .String("price", _price.Text).String("quantity", _quantity.Text).String("units", _units.Text)
                .Bool("hasBattery", _mode.SelectedId == 1 && SupportsBattery(_prototypeChoices.ElementAtOrDefault(_wanted.SelectedId)?.ID ?? ""))
                .Number("condition", _condition.SelectedId).Number("charge", _charge.SelectedId).String("description", _description.Text)
                .String("total", Money(price * quantity)).String("error", _localError.Length > 0 ? _localError : error)
                .Bool("valid", error.Length == 0));
        _panel.SetIncrementalState(Interface, data, Title ?? "");
    }
    private TguiData OfferData(TradeOfferView o) => new TguiData().String("id", o.Id.ToString()).Bool("mine", o.Mine).Bool("buy", o.BuyOrder)
        .String("name", o.Name).String("prototype", o.Prototype).String("category", o.Category).String("description", o.Description)
        .String("preview", DescriptionPreview(o.Description)).String("character", o.Character)
        .Number("price", o.Price).String("money", Money(o.Price)).Number("remaining", o.Remaining).Number("condition", o.Condition)
        .Number("charge", o.Charge).Number("units", o.StackUnits).Bool("hasBattery", HasBattery(o)).Bool("splittable", o.Splittable)
        .String("unitMoney", UnitPrice(o.Price, o.StackUnits));

    private TguiData QuoteData()
    {
        var quote = new TguiData().Bool("open", _basketQuote != null);
        if (_basketQuote is not { } q) return quote;
        return quote.String("money", Money(q.Total)).Bool("canPay", q.Total <= _state.Bank)
            .Array("lines", q.Lines.Select(l => new TguiData().String("id", l.Offer.ToString()).String("prototype", l.Prototype).String("name", l.Name)
                .String("character", l.Character).Number("units", l.Units).String("money", Money(l.Price))));
    }

    private void OnWebAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args) || args == null) return;
        switch (action)
        {
            case "commentText": _commentText = (args.String("value") ?? "")[..Math.Min(500, (args.String("value") ?? "").Length)]; Publish(); return;
            case "comment": PostComment(); return;
            case "closeGroup": CloseGroup(); return;
            case "group": SelectGroup(args.String("key") ?? ""); return;
            case "sellerPage" when args.TryInt("value", out var sellerPage): SellerPage(sellerPage); return;
            case "lotAmount": _lotAmount = (args.String("value") ?? "")[..Math.Min(10, (args.String("value") ?? "").Length)]; Publish(); return;
            case "autoAmount": _autoAmount = (args.String("value") ?? "")[..Math.Min(10, (args.String("value") ?? "").Length)]; Publish(); return;
            case "basketAdd": AddToBasket(); return;
            case "basketRemove" when Guid.TryParse(args.String("id"), out var remove): RemoveBasket(remove); return;
            case "basketQuote": QuoteBasket(false); return;
            case "basketAuto": QuoteBasket(true); return;
            case "basketConfirm": ConfirmBasket(); return;
            case "basketBack": BackBasket(); return;
            case "localSearch": LocalSearch(args.String("value") ?? ""); return;
            case "localPage" when args.TryInt("value", out var localPage):
                _localPage = Math.Clamp(localPage, 0, Math.Max(0, (LocalMatches.Length - 1) / LocalPageSize)); RenderLocalMarket(); Publish(); return;
            case "localAdd" when args.TryInt("amount", out var localAmount): LocalCart(args.String("prototype") ?? "", localAmount); return;
            case "localRemove": LocalCart(args.String("prototype") ?? "", 0, true); return;
            case "localCheckout": LocalPurchase(false); return;
            case "localConfirm": LocalPurchase(true); return;
            case "localBack": _localConfirm = false; RenderLocalMarket(); Publish(); return;
            case "section": ChangeSection(args.String("value") ?? ""); return;
            case "layout" when args.String("value") is "cards" or "list": SetCards(args.String("value") == "cards"); return;
            case "refresh": QueueRefresh(_state.Page); return;
            case "page" when args.TryInt("value", out var page): Browse(page); return;
            case "search": _search.Text = (args.String("value") ?? "")[..Math.Min(100, (args.String("value") ?? "").Length)]; Browse(); break;
            case "filter" when args.TryInt("value", out var filter) && filter is >= 0 and <= 2: _filter.SelectId(filter); Browse(); break;
            case "category" when args.TryInt("value", out var category) && category >= 0 && category < TradeCatalogue.Categories.Length: _category.SelectId(category); Browse(); break;
            case "sort" when args.TryInt("value", out var sort) && sort is >= 0 and <= 2: _sort.SelectId(sort); Browse(); break;
            case "select" when Guid.TryParse(args.String("id"), out var selected): Select(selected); return;
            case "prepare" when Guid.TryParse(args.String("id"), out var prepare): Prepare(prepare); return;
            case "confirm": Confirm(); return;
            case "back": _confirmation = null; RenderNativeDetails(); break;
            case "claim" when Guid.TryParse(args.String("id"), out var delivery): Claim(delivery); return;
            case "item":
                var index = Array.FindIndex(_state.Items, i => i.Entity.ToString() == args.String("id"));
                if (index < 0) return;
                _itemIndex = index; SyncChoices(); RenderNativeDetails(); break;
            case "mode" when args.String("value") is "sale" or "buy": _mode.SelectId(args.String("value") == "buy" ? 1 : 0); break;
            case "prototypeSearch":
                _prototypeSearch.Text = (args.String("value") ?? "")[..Math.Min(100, (args.String("value") ?? "").Length)];
                SchedulePrototypeSearch(); break;
            case "prototype":
                var choice = _prototypeChoices.FindIndex(p => p.ID == args.String("id"));
                if (choice < 0) return; _wanted.SelectId(choice); break;
            case "condition" when args.TryInt("value", out var condition) && condition is 0 or 50 or 80 or 100: _condition.SelectId(condition); break;
            case "charge" when args.TryInt("value", out var charge) && charge is 0 or 25 or 50 or 100: _charge.SelectId(charge); break;
            case "price": _price.Text = (args.String("value") ?? "")[..Math.Min(20, (args.String("value") ?? "").Length)]; break;
            case "quantity": _quantity.Text = (args.String("value") ?? "")[..Math.Min(10, (args.String("value") ?? "").Length)]; break;
            case "units": _units.Text = (args.String("value") ?? "")[..Math.Min(10, (args.String("value") ?? "").Length)]; break;
            case "description": _description.Text = (args.String("value") ?? "")[..Math.Min(500, (args.String("value") ?? "").Length)]; break;
            case "publish": Submit(); return;
            default: return;
        }
        _localError = ""; UpdateDraft(); Publish();
    }
}
