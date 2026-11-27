using Robust.Shared.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Server._DeepLagoon.Economy;
using Content.Server._NF.Bank;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Voyages;

[CVarDefs]
public sealed class VoyageCVars
{
    public static readonly CVarDef<float> TravelTimeScale = CVarDef.Create("voyages.travel_time_scale", 1f, CVar.SERVERONLY,
        "Authoritative voyage duration multiplier. Use 1 for normal rounds; values below 1 are for local testing.");
}

/// <summary>Virtual logistics, backed by real bounties, cargo escrow and treasury reservations.</summary>
public sealed partial class VoyageSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private CargoSystem _cargo = default!;
    [Dependency] private SectorEconomySystem _economy = default!;
    [Dependency] private CrashRecoverySystem _recovery = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private StationSystem _stations = default!;
    private readonly Dictionary<Guid, VoyageJob> _jobs = new();
    private readonly Dictionary<Guid, (EntityUid Station, CargoBountyData Bounty)> _bounties = new();
    private readonly HashSet<Guid> _busy = new();
    private readonly HashSet<Guid> _advancing = new();
    private readonly HashSet<string> _known = new();
    private readonly Dictionary<string, string> _icons = new();
    private readonly Dictionary<Guid, TimeSpan> _requests = new();
    private bool _ready, _loading;
    private TimeSpan _next;
    private sealed record Order(EntityUid Station, CargoBountyData Data, CargoBountyPrototype Prototype, VoyageOrderView View, SectorBankAccount Payer);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public override void Initialize() { SubscribeNetworkEvent<VoyageRequest>(OnRequest); InitializeWorld(); }
    public override void Update(float frameTime)
    {
        if (!_economy.Enabled) return;
        if (_timing.RealTime >= _nextBaskets)
        {
            _nextBaskets = _timing.RealTime + TimeSpan.FromSeconds(2);
            foreach (var expired in _cartQuotes.Where(q => q.Value.Until < _timing.RealTime).Select(q => q.Key).ToArray()) _cartQuotes.Remove(expired);
            foreach (var work in _basketLaunches.Values.Where(w => !w.Busy).ToArray()) _ = RetryBasket(work);
        }
        if (_closePending) { RetryCloseWorld(); return; }
        if (!_allowWorld) return;
        if (_ready && !_worldBusy && _timing.RealTime >= _nextWorld) _ = RetryWorld();
        if (!_ready) { if (!_loading && _timing.RealTime >= _next) { _next = _timing.RealTime + TimeSpan.FromSeconds(5); _ = Load(); } return; }
        if (_timing.RealTime < _next) return;
        _next = _timing.RealTime + TimeSpan.FromSeconds(2);
        if (_jobs.Count > 512)
            foreach (var old in _jobs.Values.Where(j => j.Paid).Take(_jobs.Count - 512).ToArray()) _jobs.Remove(old.Id);
        foreach (var expired in _purchaseQuotes.Where(q => q.Value.Until < _timing.RealTime).Select(q => q.Key).ToArray()) _purchaseQuotes.Remove(expired);
        foreach (var arrival in _arrivals.Values.Where(a => !a.Busy).ToArray()) _ = RetryCargo(arrival);
        foreach (var refund in _refunds.Values.ToArray()) _ = Refund(refund);
        foreach (var launch in _launches.Values.Where(l => !l.Busy).ToArray()) _ = RetryLaunch(launch);
        foreach (var job in _jobs.Values.Where(j => !j.Paid && (j.Delivered ? j.Return : j.Arrive) <= Now).ToArray())
            if (_advancing.Add(job.Id)) _ = Advance(job);
    }
    private async Task Load()
    {
        _loading = true;
        try { await RefreshWorld(); foreach (var job in await _db.ActiveVoyagesAsync()) _jobs[job.Id] = job; _ready = _allowWorld && !_world.Closed; }
        catch (Exception e) { Log.Warning($"Voyage reload deferred: {e.Message}"); }
        finally { _loading = false; }
    }
    private async Task Advance(VoyageJob previous)
    {
        var payment = false;
        var owner = new Robust.Shared.Network.NetUserId(previous.User);
        try
        {
            if (Now >= previous.Return)
            {
                if (!_bank.BeginExchangePayment(owner)) return;
                payment = true;
            }
            var job = await _db.AdvanceVoyageAsync(previous.Id, Now); _jobs[job.Id] = job;
            await RetryWorld();
            if (job.Paid && !job.Failed && job.Kind == 2 && !job.CargoIssued && _arrivalConsoles.TryGetValue(job.Id, out var receiving) && Exists(receiving))
                try { await DeliverCargo(job, receiving); } catch (Exception e) { Log.Warning($"Voyage import awaits collection: {e.Message}"); }
            if (job.Failed) _arrivalConsoles.Remove(job.Id);
            if (job.Paid)
            {
                if (job.Failed && job.Reward > 0) { _refunds[job.Id] = job; await Refund(job); }
                if (_bounties.Remove(job.Id, out var bounty) && !job.Failed) _cargo.CompleteVoyageBounty(bounty.Station, bounty.Bounty, "Виртуальный экипаж · " + job.Id.ToString("N")[..6]);
                await _bank.PublishExchangeBalance(new(job.User), job.Slot);
                Log.Info($"Voyage {job.Id}: delivered={job.Delivered}, returned, reward={job.Reward}, payer={(SectorBankAccount)job.Account}, owner={job.User}, profile={job.Profile}");
            }
        }
        catch (Exception e) { Log.Warning($"Voyage {previous.Id} settlement deferred: {e.Message}"); }
        finally { if (payment) _bank.EndExchangePayment(owner); _advancing.Remove(previous.Id); }
    }
    private bool CanUse(ICommonSession session, EntityUid console) => Exists(console) && session.AttachedEntity is { } actor &&
        !HasComp<IronmanComponent>(actor) && TryComp<MobStateComponent>(actor, out var mob) && mob.CurrentState == MobState.Alive &&
        _interaction.InRangeUnobstructed(actor, console) &&
        (HasComp<CargoBountyConsoleComponent>(console) && _ui.IsUiOpen(console, CargoConsoleUiKey.Bounty, actor) ||
         HasComp<CargoOrderConsoleComponent>(console) && _ui.IsUiOpen(console, CargoConsoleUiKey.Orders, actor));
    public static string Destination(SectorBankAccount account) => account switch
    { SectorBankAccount.Medical => "nereid", SectorBankAccount.Nfsd => "boreas", SectorBankAccount.Mieyo => "forge", SectorBankAccount.BlackMarket => "echo", _ => "helios" };
    private string Icon(CargoBountyPrototype prototype)
    {
        if (_icons.TryGetValue(prototype.ID, out var found)) return found;
        var tags = prototype.Entries.FirstOrDefault().Whitelist?.Tags;
        var entity = tags == null ? null : _prototypes.EnumeratePrototypes<EntityPrototype>().FirstOrDefault(p => !p.Abstract &&
            p.TryGetComponent<TagComponent>(out var t) && tags.Any(tag => Enumerable.Contains(t.Tags, tag)));
        return _icons[prototype.ID] = entity?.ID ?? "CrateGenericSteel";
    }
    private List<Order> Orders()
    {
        var result = new List<Order>(); var query = EntityQueryEnumerator<StationCargoBountyDatabaseComponent>();
        while (query.MoveNext(out var station, out var database))
            foreach (var data in database.Bounties.Take(32))
            {
                if (!_prototypes.TryIndex(data.Bounty, out var prototype) || data.FundedReward < 0 && prototype.RewardProto.Id != "SpaceCash") continue;
                var reward = data.FundedReward >= 0 ? data.FundedReward : prototype.Reward;
                if (reward <= 0 || prototype.Entries.Count == 0) continue;
                var payer = data.FundedReward >= 0 ? data.FoodAccount : prototype.VoyageAccount;
                if (payer == SectorBankAccount.Invalid) continue;
                var buyer = Loc.GetString("ledger-tab-" + payer);
                var manifest = string.Join(", ", prototype.Entries.Select(e => Loc.GetString(e.Name) + " ×" + e.Amount));
                var key = GetNetEntity(station) + ":" + data.Instance.ToString("N");
                if (_jobs.Values.Any(j => j.Order == key)) continue;
                result.Add(new(station, data, prototype, new(key, Destination(payer), data.Id, Icon(prototype), prototype.Entries.Sum(e => e.Amount), reward, buyer, manifest), payer));
                if (result.Count >= 256) return result;
            }
        return result;
    }
    private async void OnRequest(VoyageRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryGetEntity(request.Console, out var net) || net is not { } console || !CanUse(session, console)) return;
        if (!_economy.Enabled || !_ready || !_allowWorld) { SendStatus(session, request.Console, "Система рейсов ещё не готова"); return; }
        var user = session.UserId.UserId;
        if (request.Action != VoyageAction.Refresh && _requests.TryGetValue(user, out var next) && _timing.RealTime < next || !_busy.Add(user)) return;
        if (request.Action != VoyageAction.Refresh) _requests[user] = _timing.RealTime + TimeSpan.FromMilliseconds(250);
        var payment = false;
        try
        {
            if (!_bank.TryGetCharacter(session, out _, out _, out var slot)) return;
            var account = await _db.ReadEconomyAccountAsync(user, slot);
            if (!CanUse(session, console) || !_bank.IsCharacter(session, slot)) return;
            var state = await _db.ReadVoyagesAsync(user, account.Profile);
            _known.UnionWith(state.Known);
            if (!CanUse(session, console) || !_bank.IsCharacter(session, slot)) return;
            var status = "";
            if (request.Action == VoyageAction.Hire)
            {
                if (request.Operation == Guid.Empty || session.AttachedEntity is not { } actor || !HasComp<BankAccountComponent>(actor)) throw new InvalidOperationException("Нужен банковский счёт персонажа");
                if (!_bank.BeginExchangePayment(session.UserId)) throw new InvalidOperationException("Другая банковская операция ещё не завершена");
                payment = true;
                var hired = await _db.HireVoyageMarketCrewAsync(user, account.Profile, slot, request.Offer, request.Operation);
                await RefreshWorld();
                await _bank.PublishExchangeBalance(session.UserId, slot); status = "Экипаж нанят · списано " + hired.HirePrice + " спессо";
                _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), console);
            }
            else if (request.Action is VoyageAction.QuoteImportCart or VoyageAction.QuoteDeliveryCart or VoyageAction.ConfirmCart)
            {
                await CartRequest(request, session, console, account, state); return;
            }
            else if (request.Action == VoyageAction.QuotePurchase || request.Action == VoyageAction.Purchase || request.Action == VoyageAction.ClaimCargo)
            {
                await PurchaseRequest(request, session, console, account, state); return;
            }
            else if (request.Action is VoyageAction.Dispatch or VoyageAction.Patrol)
            {
                if (request.Operation == Guid.Empty || _jobs.ContainsKey(request.Operation)) { await Board(session, console, account.Profile, ""); return; }
                if (!CanUse(session, console)) return;
                var crew = state.Crews.FirstOrDefault(c => c.Id == request.Crew) ?? throw new InvalidOperationException("Экипаж не принадлежит персонажу");
                if (_jobs.Values.Any(j => j.Crew == crew.Id && !j.Paid) || _launches.Values.Any(l => l.Job.Crew == crew.Id)) throw new InvalidOperationException("Экипаж ещё не вернулся");
                var offer = VoyageRouteModel.CrewOffers.Single(o => o.Id == crew.Offer);
                Order? order = null;
                var patrol = request.Action == VoyageAction.Patrol;
                if (patrol && offer.Id != "mercenary") throw new InvalidOperationException("Нужен наёмнический отряд");
                if (!patrol && offer.Id == "mercenary") throw new InvalidOperationException("Наёмники предназначены для подавления пиратов");
                if (!request.Survey && !patrol) order = Orders().FirstOrDefault(o => o.View.Id == request.Order && o.View.Destination == request.Destination)
                    ?? throw new InvalidOperationException("Заказ уже недоступен");
                var graph = new VoyageRouteModel(); graph.SetGalaxy(_world.Seed, _world.Cycle);
                for (var i = 0; i < graph.Systems.Count; i++) if (_known.Contains(graph.Systems[i].Id)) graph.Systems[i] = graph.Systems[i] with { Known = true };
                for (var i = 0; i < graph.Lanes.Count; i++) if (graph.System(graph.Lanes[i].From).Known && graph.System(graph.Lanes[i].To).Known) graph.Lanes[i] = graph.Lanes[i] with { Charted = true };
                var path = graph.Path(VoyageRouteModel.Home, request.Destination);
                if (request.Survey)
                {
                    if (graph.System(request.Destination).Known) throw new InvalidOperationException("Маршрут уже изучен");
                    path = graph.SurveyPath(request.Destination);
                }
                if (path.Length < 2) throw new InvalidOperationException("Нет доступного маршрута");
                var seconds = Math.Max(1000L, (long)Math.Ceiling(graph.Minutes(path) * 60000d * Math.Clamp(_config.GetCVar(VoyageCVars.TravelTimeScale), .001f, 10) / ((request.Survey ? offer.SurveySpeed : offer.Speed) * (1 + .04 * (crew.Level - 1)))));
                var now = Now;
                var origin = _stations.GetOwningStation(console) is { } source ? Name(source) : Name(console);
                var job = new VoyageJob(request.Operation, user, account.Profile, slot, crew.Id, order?.View.Id ?? "", request.Destination, origin,
                    path, order?.View.Manifest ?? (patrol ? "Наёмнический рейс" : "Разведка"), order?.View.Reward ?? 0, (byte)(order?.Payer ?? SectorBankAccount.Invalid), now, now + seconds, now + seconds * 2, request.Survey, Cycle: _world.Cycle, Kind: patrol ? 3 : request.Survey ? 1 : 0, LegRisk: _world.Risk(path, now, crew.Level, patrol));
                var accepted = await Launch(console, job, order);
                status = !accepted ? "Груз зарезервирован. Ожидание подтверждения отправки" : "Груз принят. Награда — после доставки и возвращения: " + job.Reward + " спессо";
                _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), console);
            }
            await Board(session, console, account.Profile, status, request.Destination, request.Search, request.Crew);
        }
        catch (Exception e) { Log.Warning($"Voyage operation rejected/deferred: {e.Message}"); SendStatus(session, request.Console, e is InvalidOperationException ? e.Message : "Операция не завершена. Проверьте состояние рейса"); }
        finally { if (payment) _bank.EndExchangePayment(session.UserId); _busy.Remove(user); }
    }
    private void SendStatus(ICommonSession session, NetEntity console, string status) => RaiseNetworkEvent(new VoyageSnapshot { Console = console, Status = status }, session);
    private async Task Board(ICommonSession session, EntityUid console, int profile, string status, string selected = "", string search = "", Guid preferredCrew = default)
    {
        var state = await _db.ReadVoyagesAsync(session.UserId.UserId, profile);
        if (!CanUse(session, console)) return;
        _known.UnionWith(state.Known);
        var orders = Orders();
        var selectedNode = _galaxy.Systems.FirstOrDefault(s => s.Id == selected);
        var previewCrew = state.Crews.FirstOrDefault(c => c.Id == preferredCrew) ?? state.Crews.FirstOrDefault();
        var path = selectedNode == null ? [] : _galaxy.Path(VoyageRouteModel.Home, selected);
        if (selectedNode is { Known: false }) path = _galaxy.SurveyPath(selected);
        var quotes = _purchaseQuotes.GetValueOrDefault(session.UserId.UserId);
        var budgets = Enum.GetValues<SectorBankAccount>().Where(a => a != SectorBankAccount.Invalid).ToDictionary(a => Loc.GetString("ledger-tab-" + a), a => _bank.TryGetBalance(a, out var balance) ? balance : 0);
        RaiseNetworkEvent(new VoyageSnapshot
        {
            Console = GetNetEntity(console), Origin = _stations.GetOwningStation(console) is { } station ? Name(station) : Name(console), Status = status,
            Cycle = _world.Cycle, Seed = _world.Seed, Selected = selected,
            PlayerSector = EntityManager.System<SectorExpeditionSystem>().PlayerLocation(session.AttachedEntity).Sector,
            PlayerFlight = EntityManager.System<SectorExpeditionSystem>().PlayerLocation(session.AttachedEntity).Flight,
            Piracy = _world.Routes.ToDictionary(p => p.Key, p => p.Value.Value(Now)),
            Risk = Risk(path, previewCrew?.Level ?? 1, previewCrew?.Offer == "mercenary"),
            HireOffers = _world.Offers.Select(o => new VoyageHireView(o.Id, o.Type, o.Level, o.Cost)).ToArray(),
            Products = selectedNode is { Known: true } && selected != VoyageRouteModel.Home ? Supply(console, selectedNode, search) : [],
            Quote = quotes != null && quotes.Until > _timing.RealTime && quotes.Job.Profile == profile && quotes.Job.Destination == selected ? new(quotes.Job.Id, quotes.Job.Manifest, quotes.Job.PurchaseCost, quotes.Job.Quantity, quotes.Risk) : null,
            CartQuote = _cartQuotes.TryGetValue(session.UserId.UserId, out var cart) && cart.Profile == profile && cart.Console == console && cart.Until > _timing.RealTime ? cart.View : null,
            CartCommitted = _lastCartCommitted.GetValueOrDefault((session.UserId.UserId, profile)),
            CartPending = _basketLaunches.Values.FirstOrDefault(b => b.Quote.User == session.UserId.UserId && b.Quote.Profile == profile)?.Quote.Id ?? Guid.Empty,
            Bank = state.Bank, Clock = Now / 1000d, TimeScale = Math.Clamp(_config.GetCVar(VoyageCVars.TravelTimeScale), .001f, 10), Orders = orders.Select(o => o.View).ToArray(), Known = _known.ToArray(), Budgets = budgets,
            Crews = state.Crews.Select((c, i) => new VoyageCrewView(c.Id, c.Offer, i + 1, c.Level, c.Experience)).ToArray(),
            Flights = PublicFlights(session.UserId.UserId, profile).Concat(state.Parcels.Select(j => FlightView(j, session.UserId.UserId, profile))).DistinctBy(f => f.Id).ToArray(),
        }, session);
    }
    public VoyageOrderView[] SectorOrderViews() => Orders().Select(o => o.View).ToArray();
    private static VoyageFlightView FlightView(VoyageJob j, Guid user, int profile) => new(j.Id, j.User == user && j.Profile == profile, j.Crew,
        j.Manifest, j.Stops, j.Depart / 1000d, j.Arrive / 1000d, j.Return / 1000d, j.Delivered, j.Paid, j.Survey, j.Origin, j.Failed, j.Kind, j.Combat, j.CargoIssued, j.PurchaseCost);
    public VoyageFlightView[] PublicFlights(Guid user, int profile) => _jobs.Values.Where(j => !j.Paid).Concat(_jobs.Values.Where(j => j.Paid && j.User == user && j.Profile == profile).TakeLast(32))
        .Select(j => FlightView(j, user, profile)).Concat(EntityManager.System<SectorExpeditionSystem>().HumanFlights(user, profile)).ToArray();

}
