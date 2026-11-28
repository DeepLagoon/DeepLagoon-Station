using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._NF.Bank;
using Content.Server._NF.Market.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.Destructible;
using Content.Server.Preferences.Managers;
using Content.Server.Nutrition.Components;
using Content.Server.PowerCell;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared._NF.Market;
using Content.Shared.Damage;
using Content.Shared.Ghost;
using Content.Shared.Interaction;
using Content.Shared.Materials;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.Power.Components;
using Content.Shared.Preferences;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.Economy;

/// <summary>Existing market terminals provide durable escrow, private buy orders and full-state delivery.</summary>
public sealed partial class PlayerTradeSystem : EntitySystem
{
    [Dependency] private SectorEconomySystem _economy = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private CargoSystem _cargo = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private CrashRecoverySystem _recovery = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IDependencyCollection _dependencies = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    private sealed record Access(Guid Token, EntityUid Console, int Slot, int Profile, TimeSpan Until, Dictionary<Guid, Guid> Owners);
    private readonly Dictionary<NetUserId, Access> _access = new();
    private readonly HashSet<NetUserId> _reading = new();
    private readonly Dictionary<NetUserId, TimeSpan> _requests = new();
    private sealed class Work
    {
        public required ICommonSession Session;
        public required Access Access;
        public required Func<Task> Apply;
        public Action? PostCommit;
        public EntityUid? Root, Escrow;
        public EntityCoordinates Original;
        public bool Committed, Busy;
        public EntityUid[] Entities = [];
        public Guid Delivery, Lease;
        public RecoveryExternal? Recovery;
        public NetUserId? Counterparty;
        public int CounterpartySlot = -1;
        public readonly List<NetUserId> BasketLocks = new();
        public BasketSeller[] BasketSellers = [];
        public Guid[] PurchasedDeliveries = [];
        public bool ToTelepad, Automatic, Issued;
        public EntityUid? ReceivingPad;
        public TradeRequest Query = new();
    }
    private readonly Dictionary<NetUserId, Work> _work = new();
    private TimeSpan _retry;
    public override void Initialize()
    {
        SubscribeNetworkEvent<TradeRequest>(OnRequest);
        InitializeTradeLifecycle();
        SubscribeLocalEvent<Content.Shared.PDA.PdaComponent, Content.Server.CartridgeLoader.CartridgeLoaderPreinstallEvent>(InstallMarketplace);
    }
    private bool CanUse(ICommonSession session, EntityUid console) => Exists(console) &&
        session.AttachedEntity is { } actor && !HasComp<GhostComponent>(actor) &&
        TryComp<MobStateComponent>(actor, out var state) && state.CurrentState == MobState.Alive &&
        (CanBrowseMarketplace(session, console) || HasComp<MarketConsoleComponent>(console) &&
        _interaction.InRangeUnobstructed(actor, console) && _ui.IsUiOpen(console, MarketConsoleUiKey.Default, actor));
    private bool SameCharacter(Work work) => work.Session.AttachedEntity is { } &&
        _bank.IsCharacter(work.Session, work.Access.Slot);
    private void Status(ICommonSession session, EntityUid console, string key)
    {
        if (!CanUse(session, console)) return;
        if (key is "telepad_unavailable" or "telepad_busy") TelepadErrorSound(console);
        RaiseNetworkEvent(new TradeSnapshot { Console = GetNetEntity(console), Enabled = true, Status = key }, session);
    }
    private async void OnRequest(TradeRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryGetEntity(request.Console, out var target) || target is not { } console || !CanUse(session, console)) return;
        if (HasComp<Content.Shared.PDA.PdaComponent>(console) && request.Action is not (TradeAction.Refresh or TradeAction.Comment)) return;
        if (!_economy.Enabled) { Status(session, console, "disabled"); return; }
        if (!_tradeReady) { Status(session, console, _allowNewTradeCycle ? "processing" : "round_closed"); return; }
        if (_requests.TryGetValue(session.UserId, out var next) && _timing.RealTime < next) return;
        _requests[session.UserId] = _timing.RealTime + TimeSpan.FromSeconds(.5);
        if (_work.ContainsKey(session.UserId)) { Status(session, console, "processing"); return; }
        if (!_reading.Add(session.UserId)) return;
        var payment = false;
        Work? work = null;
        try
        {
            if (!_bank.TryGetCharacter(session, out _, out _, out var slot)) return;
            if (request.Action == TradeAction.Refresh) { await Board(session, console, slot, request.Search, request.Page, request.Filter, request.Sort, request.Category, request.Group, request.SellerPage, commentOffer: request.Offer); return; }
            if (_work.Count >= 64) { Status(session, console, "busy"); return; }
            if (!_access.TryGetValue(session.UserId, out var access) || access.Token != request.Token || access.Console != console ||
                access.Slot != slot || access.Until < _timing.RealTime) { await Board(session, console, slot, request.Search, request.Page, request.Filter, request.Sort, request.Category); return; }
            if (request.Action == TradeAction.Comment)
            {
                if (request.Description.Length is < 1 or > 500) throw new InvalidOperationException("invalid_comment");
                var author = MarketplaceAuthor(console);
                if (!CanBrowseMarketplace(session, console) || author.Length == 0) throw new InvalidOperationException("comment_id");
                if (!access.Owners.ContainsKey(request.Offer)) throw new InvalidOperationException("offer_missing");
                await _db.AddTradeCommentAsync(_tradeCycle, request.Offer, session.UserId.UserId, author,
                    TradeText.Plain(request.Description), SectorEconomySystem.UtcNow);
                await Board(session, console, slot, request.Search, request.Page, request.Filter, request.Sort, request.Category,
                    request.Group, request.SellerPage, commentOffer: request.Offer);
                return;
            }
            if (request.Action == TradeAction.QuoteBasket)
            {
                var plan = await _db.QuoteTradeBasketAsync(new(session.UserId.UserId, access.Profile, _tradeCycle,
                    request.Lines.Select(l => new BasketSelection(l.Offer, l.Units)).ToArray(), request.Prototype, request.Quantity,
                    _prototypes.TryIndex<EntityPrototype>(request.Prototype, out var requestedType) && requestedType.Name.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ? "" : request.Search), SectorEconomySystem.UtcNow);
                if (!_tradeReady || plan.Cycle != _tradeCycle) throw new InvalidOperationException("round_closed");
                _basketQuotes[session.UserId] = plan;
                await Board(session, console, slot, request.Search, request.Page, request.Filter, request.Sort, request.Category, request.Group, request.SellerPage,
                    new(plan.Id, plan.Rows.Sum(r => r.Price), plan.Rows.Select(r => new TradeQuoteLineView(r.Offer, r.Prototype, r.Name, r.Character, r.Units, r.Price)).ToArray()));
                return;
            }
            if (!_bank.BeginExchangePayment(session.UserId)) { Status(session, console, "busy"); return; }
            payment = true;
            work = new Work { Session = session, Access = access, Apply = () => Task.CompletedTask, Query = request };
            work.ToTelepad = UsesTelepad(console);
            if (work.ToTelepad && (request.Action is TradeAction.PurchaseBasket or TradeAction.Claim || request.Action == TradeAction.Deal && request.Item == null) && !_cargo.HasMarketTelepad(console))
                throw new InvalidOperationException("telepad_unavailable");
            var operation = Guid.NewGuid();
            TradeItem? item = null;
            RecoveryRemoval? removal = null;
            if (request.Action == TradeAction.Sell || request.Action == TradeAction.Deal && request.Item != null)
            {
                if (request.Item is not { } net || !TryGetEntity(net, out var entity) || entity is not { } root ||
                    !_cargo.TradingPalletItems(console).Contains(root)) throw new InvalidOperationException("invalid_item");
                var entities = Bundle(root);
                item = Capture(root, entities);
                removal = _recovery.ConsumedItems(entities);
                work.Root = root;
                work.Entities = entities;
                work.Original = Transform(root).Coordinates;
                work.Escrow = Spawn(null, MapCoordinates.Nullspace);
                EntityManager.System<MetaDataSystem>().SetEntityPaused(work.Escrow.Value, true);
                _recovery.RegisterEscrow(work.Escrow.Value, session.AttachedEntity!.Value);
                if (!_containers.Insert(root, _containers.EnsureContainer<Container>(work.Escrow.Value, "market"))) throw new InvalidOperationException("busy");
            }
            switch (request.Action)
            {
                case TradeAction.PurchaseBasket:
                    if (!_basketQuotes.TryGetValue(session.UserId, out var quoted) || quoted.Id != request.Quote || quoted.Profile != access.Profile || quoted.Cycle != _tradeCycle)
                        throw new InvalidOperationException("quote_expired");
                    foreach (var seller in quoted.Rows.Select(r => new NetUserId(r.User)).Distinct().OrderBy(u => u.UserId))
                    {
                        if (!_bank.BeginExchangePayment(seller)) throw new InvalidOperationException("busy");
                        work.BasketLocks.Add(seller);
                    }
                    var purchase = new BasketPurchase(operation, quoted);
                    var basketWork = work;
                    work.Apply = async () =>
                    {
                        var result = await _db.PurchaseTradeBasketAsync(purchase, SectorEconomySystem.UtcNow);
                        basketWork.BasketSellers = result.Sellers;
                        basketWork.PurchasedDeliveries = result.Deliveries;
                        basketWork.PostCommit = () => _basketQuotes.Remove(session.UserId);
                    };
                    break;
                case TradeAction.Sell:
                case TradeAction.BuyOrder:
                    var prototype = item?.Prototype ?? request.Prototype;
                    if (!_prototypes.TryIndex<EntityPrototype>(prototype, out var proto) || proto.Abstract) throw new InvalidOperationException("invalid_item");
                    var create = new TradeCreate(operation, session.UserId.UserId, access.Profile, request.Action == TradeAction.BuyOrder,
                        prototype, item?.Name ?? proto.Name, request.Description, request.Action == TradeAction.Sell ? 1 : request.Quantity,
                        request.Price, new(request.Condition, request.Charge, request.StackUnits), item, removal) { Cycle = _tradeCycle };
                    work.Apply = async () => { await _db.CreateTradeOfferAsync(create, SectorEconomySystem.UtcNow); };
                    break;
                case TradeAction.Deal:
                    if (!access.Owners.TryGetValue(request.Offer, out var ownerId)) throw new InvalidOperationException("offer_missing");
                    if (ownerId == session.UserId.UserId) throw new InvalidOperationException("self_trade");
                    var ownerUser = new NetUserId(ownerId);
                    if (!_bank.BeginExchangePayment(ownerUser)) throw new InvalidOperationException("busy");
                    work.Counterparty = ownerUser;
                    var deal = new TradeDeal(operation, session.UserId.UserId, access.Profile, request.Offer, item, removal);
                    var tradeWork = work;
                    work.Apply = async () =>
                    {
                        var result = await _db.ExecuteTradeAsync(deal, SectorEconomySystem.UtcNow);
                        tradeWork.CounterpartySlot = result.CounterpartySlot;
                        if (item == null && result.Delivery is { } delivery) tradeWork.PurchasedDeliveries = [delivery];
                        tradeWork.PostCommit = () =>
                        {
                            if (result.Applied) EntityManager.System<Content.Server._DeepLagoon.Exchange.ExchangeSystem>()
                                .ObservePlayerTrade(result.TradedPrototype, result.Price, result.Materials);
                        };
                    };
                    break;
                case TradeAction.Cancel:
                    work.Apply = async () => { await _db.CancelTradeOfferAsync(session.UserId.UserId, access.Profile, request.Offer, operation, SectorEconomySystem.UtcNow); };
                    break;
                case TradeAction.Claim:
                    if (_automaticDeliveries.TryGetValue(request.Offer, out var queued))
                    {
                        if (queued.Session.UserId != session.UserId || queued.Access.Profile != access.Profile) throw new InvalidOperationException("delivery_missing");
                        if (queued.Busy) throw new InvalidOperationException("busy");
                        ReleaseDeliveryPad(queued, false);
                        queued.Access = access; queued.Session = session;
                        queued.ToTelepad = work.ToTelepad;
                        Status(session, console, "processing"); return;
                    }
                    work.Delivery = request.Offer; work.Lease = operation;
                    var deliveryWork = work;
                    work.Apply = () => Claim(deliveryWork);
                    break;
                default: throw new InvalidOperationException("invalid_order");
            }
            _work.Add(session.UserId, work);
            payment = false;
            _access.Remove(session.UserId);
            await Process(work);
        }
        catch (Exception e)
        {
            if (work != null) Restore(work);
            Status(session, console, IsRejection(e) ? e.Message : "unavailable");
        }
        finally
        {
            if (payment)
            {
                _bank.EndExchangePayment(session.UserId);
                if (work?.Counterparty is { } owner) _bank.EndExchangePayment(owner);
                if (work != null) foreach (var seller in work.BasketLocks) _bank.EndExchangePayment(seller);
            }
            _reading.Remove(session.UserId);
        }
    }

    private async Task Board(ICommonSession session, EntityUid console, int slot, string search, int page,
        TradeFilter filter = TradeFilter.All, TradeSort sort = TradeSort.Newest, string category = "", string group = "", int sellerPage = 0, TradeQuoteView? quote = null, bool purchased = false, Guid commentOffer = default)
    {
        var generation = _tradeGeneration;
        var account = await _db.ReadEconomyAccountAsync(session.UserId.UserId, slot);
        var board = await _db.ReadTradeBoardAsync(session.UserId.UserId, account.Profile);
        if (generation != _tradeGeneration || !_tradeReady) return;
        if (!CanUse(session, console) || !_bank.IsCharacter(session, slot)) return;
        search = search.Length > 100 ? search[..100] : search;
        var offers = SelectOffers(board.Offers, session.UserId.UserId, account.Profile, search, filter, sort, category);
        var groups = offers.GroupBy(o => TradeCatalogue.GroupKey(o.Prototype, o.BuyOrder)).Select(g =>
        {
            var first = g.First();
            var prototype = TradeCatalogue.Commodity(first.Prototype) ?? first.Prototype;
            var material = TradeCatalogue.Commodity(prototype) != null;
            var cheapest = g.OrderBy(o => (decimal)o.Price / (material ? o.Criteria.StackUnits : 1)).ThenBy(o => o.Created).First();
            return new TradeGroupView(g.Key, prototype, _prototypes.TryIndex<EntityPrototype>(prototype, out var type) ? type.Name : first.ItemName,
                first.BuyOrder, cheapest.Price, material ? cheapest.Criteria.StackUnits : 1);
        }).ToArray();
        if (sort != TradeSort.Newest) groups = (sort == TradeSort.Cheapest ? groups.OrderBy(g => (decimal)g.Price / g.PriceUnits) : groups.OrderByDescending(g => (decimal)g.Price / g.PriceUnits)).ToArray();
        var pages = Math.Max(1, ((filter == TradeFilter.Mine ? offers.Length : groups.Length) + 19) / 20);
        page = Math.Clamp(page, 0, pages - 1);
        var pageGroups = filter == TradeFilter.Mine ? [] : groups.Skip(page * 20).Take(20).ToArray();
        if (!pageGroups.Any(g => g.Key == group)) group = pageGroups.FirstOrDefault()?.Key ?? "";
        var selectedOffers = filter == TradeFilter.Mine ? offers : offers.Where(o => TradeCatalogue.GroupKey(o.Prototype, o.BuyOrder) == group)
            .OrderBy(o => (decimal)o.Price / (TradeCatalogue.Commodity(o.Prototype) != null ? o.Criteria.StackUnits : 1)).ThenBy(o => o.Created).ThenBy(o => o.Id).ToArray();
        var sellerPages = Math.Max(1, (selectedOffers.Length + 19) / 20);
        sellerPage = Math.Clamp(filter == TradeFilter.Mine ? page : sellerPage, 0, sellerPages - 1);
        var visible = selectedOffers.Skip(sellerPage * 20).Take(20).ToArray();
        var access = new Access(Guid.NewGuid(), console, slot, account.Profile, _timing.RealTime + TimeSpan.FromSeconds(60),
            visible.ToDictionary(o => o.Id, o => o.User));
        _access[session.UserId] = access;
        var readOnly = HasComp<Content.Shared.PDA.PdaComponent>(console);
        var author = readOnly ? MarketplaceAuthor(console) : "";
        if (!visible.Any(o => o.Id == commentOffer)) commentOffer = visible.FirstOrDefault()?.Id ?? Guid.Empty;
        var comments = commentOffer == Guid.Empty ? [] : await _db.ReadTradeCommentsAsync(commentOffer, SectorEconomySystem.UtcNow);
        if (generation != _tradeGeneration || !_tradeReady || !CanUse(session, console) || !_bank.IsCharacter(session, slot)) return;
        // Re-read after awaiting the database: an inserted ID can change while a board is loading.
        author = readOnly ? MarketplaceAuthor(console) : "";
        var items = new List<TradeItemView>();
        foreach (var uid in readOnly ? Enumerable.Empty<EntityUid>() : _cargo.TradingPalletItems(console).Take(64))
            try { var view = Describe(uid); Bundle(uid); items.Add(new(GetNetEntity(uid), view.Name, view.Prototype, view.Condition, view.Charge, view.StackUnits) { HasBattery = view.HasBattery }); }
            catch (InvalidOperationException) { }
        RaiseNetworkEvent(new TradeSnapshot { Console = GetNetEntity(console), Token = access.Token, Enabled = true, Bank = board.Bank,
            Page = page, Pages = pages, Total = offers.Length, Status = "ready", Items = items.ToArray(),
            ReadOnly = readOnly, CanComment = author.Length > 0, CommentAuthor = author, CommentOffer = commentOffer,
            Comments = comments.Select(c => new TradeCommentView(c.Author, c.Text, c.Time)).ToArray(),
            Groups = pageGroups, Group = group, SellerPage = sellerPage, SellerPages = sellerPages, Quote = quote, BasketPurchased = purchased,
            Offers = visible.Select(o => new TradeOfferView(o.Id, o.User == session.UserId.UserId,
                o.BuyOrder, o.Character, _prototypes.TryIndex<EntityPrototype>(o.Prototype, out var type) && type.Name != o.ItemName
                    ? o.ItemName + " (" + type.Name + ")" : o.ItemName, o.Description, o.Price, o.Remaining, o.Criteria.MinimumCondition,
                o.Criteria.MinimumCharge, o.Criteria.StackUnits, o.Expires) { Prototype = o.Prototype, Category = Category(o.Prototype), HasBattery = o.HasBattery,
                    Splittable = o.Splittable, OriginalPrice = o.OriginalPrice, OriginalUnits = o.OriginalUnits }).ToArray(),
            Deliveries = readOnly ? [] : board.Deliveries.Take(100).Select(d => new TradeDeliveryView(d.Id, d.Name) { Prototype = d.Prototype }).ToArray() }, session);
    }

    private string Category(string prototype) => _prototypes.TryIndex<EntityPrototype>(prototype, out var item) ? TradeCatalogue.Category(item) : "other";

    public TradeOffer[] SelectOffers(IEnumerable<TradeOffer> source, Guid user, int profile, string search, TradeFilter filter, TradeSort sort, string category)
    {
        var query = source.Where(o => !o.Closed && (o.User != user || o.Profile == profile) && o.Expires > SectorEconomySystem.UtcNow &&
            (search.Length == 0 || o.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase) || o.Description.Contains(search, StringComparison.OrdinalIgnoreCase) || o.Character.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                _prototypes.TryIndex<EntityPrototype>(o.Prototype, out var item) && item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (category.Length == 0 || Category(o.Prototype) == category) &&
            (filter != TradeFilter.Mine || o.User == user) && (filter != TradeFilter.Sale || !o.BuyOrder) && (filter != TradeFilter.Buy || o.BuyOrder));
        return (sort switch
        {
            TradeSort.Cheapest => query.OrderBy(o => o.Price).ThenByDescending(o => o.Created),
            TradeSort.MostExpensive => query.OrderByDescending(o => o.Price).ThenByDescending(o => o.Created),
            _ => query.OrderByDescending(o => o.Created),
        }).ThenBy(o => o.Id).ToArray();
    }
    private async Task Process(Work work)
    {
        if (work.Busy) return;
        work.Busy = true;
        try
        {
            if (work.ToTelepad && work.Delivery != Guid.Empty && !work.Issued && !ReserveDeliveryPad(work))
                throw new InvalidOperationException("telepad_busy");
            if (!work.Committed)
            {
                await work.Apply();
                work.Committed = true;
                if (work.Delivery == Guid.Empty && work.Root is { } sold && Exists(sold)) Del(sold);
                var postCommit = work.PostCommit;
                work.PostCommit = null;
                postCommit?.Invoke();
            }
            if (work.Delivery != Guid.Empty && !work.Issued && work.Root is { } delivered)
            {
                if (!work.ToTelepad)
                {
                    if (!SameCharacter(work)) return;
                    var account = await _db.ReadEconomyAccountAsync(work.Session.UserId.UserId, work.Access.Slot);
                    if (account.Profile != work.Access.Profile || !SameCharacter(work)) return;
                }
                if (work.ToTelepad && (work.ReceivingPad is not { } target || !_cargo.ValidMarketArrival(target, work.Lease)))
                    throw new InvalidOperationException("telepad_unavailable");
                _recovery.AcceptExternalDelivery(work.Recovery, work.Entities);
                _containers.TryRemoveFromContainer(delivered, force: true);
                _transform.SetCoordinates(delivered, Transform(work.ToTelepad ? work.ReceivingPad!.Value : work.Session.AttachedEntity!.Value).Coordinates);
                work.Issued = true;
                ReleaseDeliveryPad(work, true);
            }
            if (!work.Automatic) await _bank.PublishExchangeBalance(work.Session.UserId, work.Access.Slot);
            if (work.Counterparty is { } owner && work.CounterpartySlot >= 0)
                await _bank.PublishExchangeBalance(owner, work.CounterpartySlot);
            foreach (var seller in work.BasketSellers) await _bank.PublishExchangeBalance(new NetUserId(seller.User), seller.Slot);
            Finish(work);
            if (work.ToTelepad && work.PurchasedDeliveries.Length > 0)
            {
                PurchaseSound(work.Access.Console);
                QueueDeliveries(work);
            }
            if (!_work.ContainsKey(work.Session.UserId) && CanUse(work.Session, work.Access.Console)) await Board(work.Session, work.Access.Console, work.Access.Slot,
                work.Query.Search, work.Query.Page, work.Query.Filter, work.Query.Sort, work.Query.Category, work.Query.Group, work.Query.SellerPage,
                purchased: !work.Automatic && work.Query.Action == TradeAction.PurchaseBasket);
        }
        catch (Exception e) when (work.Automatic && !work.Committed && e is InvalidOperationException &&
                                  e.Message is "telepad_busy" or "telepad_unavailable" or "recovery_unavailable" or "delivery_busy")
        {
            ReleaseDeliveryPad(work, false);
        }
        catch (Exception e) when (!work.Committed && IsRejection(e))
        {
            // Only explicit authoritative business rejections release the original item.
            Restore(work); Finish(work); Status(work.Session, work.Access.Console, e.Message);
        }
        catch (Exception e)
        {
            Log.Warning($"Market transaction awaits confirmation ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}).");
            Status(work.Session, work.Access.Console, "processing");
        }
        finally { work.Busy = false; }
    }
    private static bool IsRejection(Exception e) => (e is InvalidOperationException or ArgumentException) && e.Message is
        "invalid_item" or "invalid_offer" or "invalid_order" or "offer_limit" or "offer_missing" or "offer_closed" or
        "own_offer" or "self_trade" or "item_mismatch" or "supply_item" or "insufficient_spessos" or "character_missing" or
        "delivery_missing" or "delivery_busy" or "recovery_unavailable" or "invalid_snapshot" or "busy" or "not_owner" or "round_closed" or
        "invalid_basket" or "basket_changed" or "basket_stock" or "basket_limit" or "quote_expired" or "telepad_unavailable" or "telepad_busy" or "comment_id" or "comment_rate" or "invalid_comment";
    private void Finish(Work work)
    {
        ReleaseDeliveryPad(work, false);
        if (work.Automatic) _automaticDeliveries.Remove(work.Delivery);
        else { _work.Remove(work.Session.UserId); _bank.EndExchangePayment(work.Session.UserId); }
        if (work.Counterparty is { } owner) _bank.EndExchangePayment(owner);
        foreach (var seller in work.BasketLocks) _bank.EndExchangePayment(seller);
        if (work.Escrow is { } escrow && Exists(escrow)) QueueDel(escrow);
    }
    private void Restore(Work work)
    {
        if (work.Root is { } root && Exists(root))
        {
            if (work.Delivery != Guid.Empty) Del(root);
            else { _containers.TryRemoveFromContainer(root, force: true); _transform.SetCoordinates(root, work.Original); }
        }
        if (work.Escrow is { } escrow && Exists(escrow)) QueueDel(escrow);
    }
    public override void Update(float frameTime)
    {
        UpdateTradeLifecycle();
        UpdateDeliveries();
        if (_timing.RealTime < _retry) return;
        _retry = _timing.RealTime + TimeSpan.FromSeconds(5);
        foreach (var user in _basketQuotes.Where(p => p.Value.Until <= SectorEconomySystem.UtcNow).Select(p => p.Key).Take(64).ToArray()) _basketQuotes.Remove(user);
        foreach (var work in _work.Values.Where(w => !w.Busy).Take(8).ToArray()) _ = Process(work);
    }

    private EntityUid[] Bundle(EntityUid root)
    {
        var result = new HashSet<EntityUid>();
        var pending = new Stack<EntityUid>(); pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (HasComp<Robust.Shared.Audio.Components.AudioComponent>(uid)) continue;
            if (!result.Add(uid)) continue;
            if (HasComp<Content.Server.Salvage.ExpeditionSalvageComponent>(uid))
                throw new InvalidOperationException("invalid_item");
            if (result.Count > 512 || !Exists(uid) || HasComp<ActorComponent>(uid) || HasComp<MobStateComponent>(uid) ||
                HasComp<Content.Server.Ghost.Roles.Components.GhostRoleComponent>(uid) || HasComp<MapComponent>(uid) || HasComp<Robust.Shared.Map.Components.MapGridComponent>(uid) ||
                TryComp<StackComponent>(uid, out var stack) && stack.StackTypeId.Id is "Credit" or "Bitcoin" ||
                TryComp<CryptoFarmComponent>(uid, out var farm) && farm.HasPendingMining)
                throw new InvalidOperationException("invalid_item");
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) pending.Push(child);
        }
        return result.ToArray();
    }
    private TradeItem Describe(EntityUid root)
    {
        var proto = MetaData(root).EntityPrototype ?? throw new InvalidOperationException("invalid_item");
        var condition = 100;
        if (TryComp<DamageableComponent>(root, out var damage) && damage.TotalDamage > 0)
        {
            var maximum = EntityManager.System<DestructibleSystem>().DestroyedAt(root).Double();
            condition = maximum > 0 ? (int)Math.Clamp(100 * (1 - damage.TotalDamage.Double() / maximum), 0, 100) : 0;
        }
        if (TryComp<FoodComponent>(root, out var food) &&
            proto.TryComp<SolutionContainerManagerComponent>(out var expectedSolutions, EntityManager.ComponentFactory) &&
            expectedSolutions.Solutions?.GetValueOrDefault(food.Solution) is { } expected)
        {
            double Nutrients(Content.Shared.Chemistry.Components.Solution solution) => solution.Contents
                .Where(r => r.Reagent.Prototype.Id is "Nutriment" or "Protein" or "Vitamin").Sum(r => r.Quantity.Double());
            var maximum = Nutrients(expected);
            if (maximum > 0)
                condition = Math.Min(condition, _solutions.TryGetSolution(root, food.Solution, out _, out var actual)
                    ? (int)Math.Clamp(Nutrients(actual) / maximum * 100, 0, 100) : 0);
        }
        if (!TryComp<BatteryComponent>(root, out var battery)) _cells.TryGetBatteryFromSlot(root, out battery);
        var charge = battery is { MaxCharge: > 0 }
            ? (int)Math.Clamp(100 * battery.CurrentCharge / battery.MaxCharge, 0, 100) : 0;
        var count = TryComp<StackComponent>(root, out var stack) ? stack.Count : 1;
        var materials = TryComp<PhysicalCompositionComponent>(root, out var composition)
            ? composition.MaterialComposition.ToDictionary(p => p.Key.Id, p => (double)p.Value * count) : new Dictionary<string, double>();
        return new(proto.ID, Name(root), "", condition, charge, count, materials) { HasBattery = battery != null };
    }
    private TradeItem Capture(EntityUid root, EntityUid[] entities)
    {
        var inside = entities.ToHashSet();
        var serializer = new EntitySerializer(_dependencies, new SerializationOptions { Category = FileCategory.Entity,
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false });
        serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serialize) => serialize &= inside.Contains(entity.Owner);
        serializer.SerializerProvider.RegisterSerializer(typeof(RecoveryDictionaryWriter<,>));
        serializer.SerializeEntityRecursive([root]);
        foreach (var data in serializer.EntityData.Values)
            if (data.Node.TryGet<SequenceDataNode>("components", out var components))
                foreach (var component in components.Cast<MappingDataNode>().ToArray())
                    if (component.Get<ValueDataNode>("type").Value is "RecoveryIdentity" or "ParkingOriginal") components.Remove(component);
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(serializer.Write().ToYaml())).Save(new YamlMappingFix(new Emitter(writer)), false);
        var snapshot = writer.ToString();
        if (snapshot.Length > 2000000) throw new InvalidOperationException("invalid_item");
        var item = Describe(root);
        var commodity = TradeCatalogue.Commodity(item.Prototype);
        var plain = IsPlainCommodity(root, entities, item);
        return item with { Snapshot = snapshot, Splittable = plain, Prototype = plain ? commodity! : item.Prototype };
    }
    private async Task Claim(Work work)
    {
        if (work.Root == null)
        {
            if (!_recovery.ExternalDeliveriesReady) throw new InvalidOperationException("recovery_unavailable");
            var delivery = await _db.ClaimTradeDeliveryAsync(work.Session.UserId.UserId, work.Access.Profile, work.Delivery, work.Lease, SectorEconomySystem.UtcNow);
            if (!_loader.TryLoadEntity(new StringReader(delivery.Snapshot), "market delivery", out var loaded,
                    new DeserializationOptions { LogInvalidEntities = false })) throw new InvalidOperationException("invalid_snapshot");
            var root = loaded.Value.Owner;
            work.Root = root;
            work.Entities = Bundle(root);
            if (delivery.SplitStack)
            {
                if (work.Entities.Any(uid => uid != root && !HasComp<Content.Shared.Chemistry.Components.SolutionComponent>(uid)) ||
                    !TryComp<StackComponent>(root, out var stack) || delivery.StackUnits < 1 || delivery.StackUnits > stack.Count)
                    throw new InvalidOperationException("invalid_snapshot");
                EntityManager.System<SharedStackSystem>().SetCount(root, delivery.StackUnits, stack);
                if (stack.Count != delivery.StackUnits) throw new InvalidOperationException("invalid_snapshot");
            }
            work.Escrow = Spawn(null, MapCoordinates.Nullspace);
            EntityManager.System<MetaDataSystem>().SetEntityPaused(work.Escrow.Value, true);
            if (!_containers.Insert(root, _containers.EnsureContainer<Container>(work.Escrow.Value, "delivery"))) throw new InvalidOperationException("invalid_snapshot");
            foreach (var uid in work.Entities)
                if (TryComp<CryptoFarmComponent>(uid, out var farm) && farm.WalletUser != work.Session.UserId.UserId)
                { farm.WalletUser = Guid.Empty; farm.WalletProfileId = 0; farm.WalletCharacter = ""; }
            work.Recovery = _recovery.CaptureExternalDelivery(work.Session.UserId.UserId, work.Access.Profile, work.Entities);
        }
        if (work.ToTelepad && (work.ReceivingPad is not { } pad || !_cargo.ValidMarketTelepad(work.Access.Console, pad, work.Lease)))
            throw new InvalidOperationException("telepad_unavailable");
        await _db.CompleteTradeDeliveryAsync(work.Session.UserId.UserId, work.Access.Profile, work.Delivery, work.Lease, work.Recovery);
    }
}
