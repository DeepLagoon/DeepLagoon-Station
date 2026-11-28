using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared._NF.Bank.Components;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class VoyageSystem
{
    [Dependency] private Content.Shared.Access.Systems.AccessReaderSystem _purchaseAccess = default!;
    private sealed record PurchaseQuote(VoyageJob Job, double Risk, TimeSpan Until, EntityUid Console);
    private readonly Dictionary<Guid, PurchaseQuote> _purchaseQuotes = new();
    private readonly Dictionary<Guid, EntityUid> _arrivalConsoles = new();
    private sealed class CargoArrival
    {
        public required VoyageJob Job;
        public required EntityUid Console, Escrow;
        public required EntityUid[] Roots, Entities;
        public Guid Lease = Guid.NewGuid();
        public RecoveryExternal? Recovery;
        public EntityUid? Pad;
        public bool Busy, Committed;
    }
    private readonly Dictionary<Guid, CargoArrival> _arrivals = new();
    private IEnumerable<CargoProductPrototype> Products(EntityUid console) => _prototypes.EnumeratePrototypes<CargoProductPrototype>()
        .Where(p => !p.Abstract && (TryComp<CargoOrderConsoleComponent>(console, out var order) ? order.AllowedGroups.Contains(p.Group) : p.Group == "market"));
    private long SupplyPrice(CargoProductPrototype product, string destination) => Math.Max(1, (long)Math.Ceiling(
        _economy.Quote("cargo:" + product.Product.Id, product.Cost, _prototypes.Index<EntityPrototype>(product.Product)) *
        VoyageGalaxy.PriceFactor(_galaxy.System(destination).Position)));
    public VoyageProductView[] SectorProducts(EntityUid console, string sector) => Supply(console, _galaxy.System(sector), "");
    private VoyageProductView[] Supply(EntityUid console, VoyageDestination node, string search)
    {
        var products = Products(console).ToArray();
        if (node.MarketCategory.Length > 0)
        {
            var specialized = products.Where(p => p.Category.Contains(node.MarketCategory, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (specialized.Length > 0) products = specialized;
        }
        return products.Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.ID.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(80)
            .Select(p => new VoyageProductView(p.ID, p.Product.Id, p.Name, SupplyPrice(p, node.Id))).ToArray();
    }
    private async Task PurchaseRequest(VoyageRequest request, ICommonSession session, EntityUid console, EconomyAccount account, VoyageAccountState state)
    {
        var user = session.UserId.UserId;
        if (request.Action == VoyageAction.ClaimCargo)
        {
            var parcel = state.Parcels.FirstOrDefault(j => j.Id == request.Operation) ?? throw new InvalidOperationException("Груз ещё не прибыл или уже получен");
            await DeliverCargo(parcel, console);
            await Board(session, console, account.Profile, "Груз передан", request.Destination, request.Search, request.Crew); return;
        }
        if (session.AttachedEntity is not { } buyer || !_purchaseAccess.IsAllowed(buyer, console)) throw new InvalidOperationException("Нет доступа к закупкам этой консоли");
        if (request.Action == VoyageAction.QuotePurchase)
        {
            var node = _galaxy.Systems.FirstOrDefault(s => s.Id == request.Destination && s.Known && s.Id != VoyageRouteModel.Home)
                ?? throw new InvalidOperationException("Сначала исследуйте сектор");
            var crew = state.Crews.FirstOrDefault(c => c.Id == request.Crew) ?? throw new InvalidOperationException("Выберите экипаж");
            if (crew.Offer == "mercenary") throw new InvalidOperationException("Нужен транспортный экипаж");
            if (_jobs.Values.Any(j => j.Crew == crew.Id && !j.Paid)) throw new InvalidOperationException("Экипаж занят");
            var product = Products(console).FirstOrDefault(p => p.ID == request.Product) ?? throw new InvalidOperationException("Этот товар недоступен консоли");
            if (!Supply(console, node, product.Name).Any(p => p.Id == product.ID)) throw new InvalidOperationException("В секторе нет этого товара");
            var quantity = Math.Clamp(request.Quantity, 1, 5);
            var cost = checked(SupplyPrice(product, node.Id) * quantity);
            if (cost > account.Bank || cost > int.MaxValue) throw new InvalidOperationException("Недостаточно денег для закупки");
            var path = _galaxy.Path(VoyageRouteModel.Home, node.Id);
            var offer = VoyageRouteModel.CrewOffers.Single(o => o.Id == crew.Offer);
            var duration = Math.Max(1000L, (long)Math.Ceiling(_galaxy.Minutes(path) * 60000d * Math.Clamp(_config.GetCVar(VoyageCVars.TravelTimeScale), .001f, 10) / (offer.Speed * (1 + .04 * (crew.Level - 1)))));
            var now = Now; var id = Guid.NewGuid();
            var origin = _stations.GetOwningStation(console) is { } station ? Name(station) : Name(console);
            var job = new VoyageJob(id, user, account.Profile, account.Slot, crew.Id, "", node.Id, origin, path, product.Name + " ×" + quantity,
                0, 0, now, now + duration, now + duration * 2, false, Cycle: _world.Cycle, Kind: 2,
                Product: product.Product.Id, Quantity: quantity, PurchaseCost: cost, LegRisk: _world.Risk(path, now, crew.Level, false));
            _purchaseQuotes[user] = new(job, Risk(path, crew.Level, false), _timing.RealTime + TimeSpan.FromSeconds(30), console);
            await Board(session, console, account.Profile, "", node.Id, request.Search, request.Crew); return;
        }
        if (!_purchaseQuotes.TryGetValue(user, out var quote) || quote.Job.Id != request.Quote || quote.Console != console || quote.Job.Profile != account.Profile || quote.Until < _timing.RealTime || quote.Job.Cycle != _world.Cycle || quote.Job.Destination != request.Destination)
            throw new InvalidOperationException("Расчёт устарел. Рассчитайте закупку заново");
        if (session.AttachedEntity is not { } actor || !HasComp<BankAccountComponent>(actor)) throw new InvalidOperationException("Нужен банковский счёт");
        if (!_bank.BeginExchangePayment(session.UserId)) throw new InvalidOperationException("Другая банковская операция ещё не завершена");
        try
        {
            var offset = quote.Job.Arrive - quote.Job.Depart; var now = Now;
            var job = quote.Job with { Depart = now, Arrive = now + offset, Return = now + offset * 2 };
            if (_jobs.ContainsKey(job.Id)) { await Board(session, console, account.Profile, "Рейс уже отправлен", job.Destination, request.Search); return; }
            if (_jobs.Values.Any(j => j.Crew == job.Crew && !j.Paid) || _launches.Values.Any(j => j.Job.Crew == job.Crew)) throw new InvalidOperationException("Экипаж занят");
            await Launch(console, job, null); _arrivalConsoles[job.Id] = console;
            await _bank.PublishExchangeBalance(session.UserId, account.Slot);
            _economy.Purchased("cargo:" + job.Product, job.Quantity);
            _purchaseQuotes.Remove(user);
            _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), console);
            await Board(session, console, account.Profile, "Закупка оплачена. Груз прибудет после обратного рейса", job.Destination, request.Search);
        }
        finally { _bank.EndExchangePayment(session.UserId); }
    }
    private async Task DeliverCargo(VoyageJob job, EntityUid console)
    {
        if (_arrivals.TryGetValue(job.Id, out var existing)) { await FinishCargo(existing); return; }
        if (!_recovery.ExternalDeliveriesReady) throw new InvalidOperationException("Сохранение груза ещё не готово");
        var work = new CargoArrival { Job = job, Console = console, Escrow = Spawn(null, MapCoordinates.Nullspace), Roots = [], Entities = [] };
        try
        {
            var container = _containers.EnsureContainer<Container>(work.Escrow, "imports");
            var roots = new List<EntityUid>(); var entities = new HashSet<EntityUid>();
            void Bundle(EntityUid uid)
            {
                if (!entities.Add(uid) || entities.Count > 512) return;
                if (TryComp<ContainerManagerComponent>(uid, out var cm))
                    foreach (var nested in cm.Containers.Values.SelectMany(c => c.ContainedEntities)) Bundle(nested);
            }
            foreach (var line in job.Cargo ?? [new VoyageCargoLine(job.Product, job.Manifest, job.Quantity, 0)])
            for (var i = 0; i < line.Quantity; i++)
            {
                var root = Spawn(line.Prototype, MapCoordinates.Nullspace); roots.Add(root);
                if (!_containers.Insert(root, container)) throw new InvalidOperationException("Груз нельзя передать");
                Bundle(root);
            }
            if (entities.Count > 512) throw new InvalidOperationException("Слишком сложный груз для доставки");
            work.Roots = roots.ToArray(); work.Entities = entities.ToArray();
            work.Recovery = _recovery.CaptureExternalDelivery(job.User, job.Profile, work.Entities);
            _arrivals.Add(job.Id, work);
        }
        catch { QueueDel(work.Escrow); throw; }
        await FinishCargo(work);
    }
    private async Task RetryCargo(CargoArrival work)
    {
        try { await FinishCargo(work); } catch (Exception e) { Log.Warning($"Voyage cargo {work.Job.Id} deferred: {e.Message}"); }
    }
    private async Task FinishCargo(CargoArrival work)
    {
        if (work.Busy) return; work.Busy = true;
        try
        {
            if (!Exists(work.Console)) throw new InvalidOperationException("Консоль отправления недоступна");
            if (_cargo.HasMarketTelepad(work.Console))
            {
                if (!_cargo.TryReserveMarketTelepad(work.Console, work.Lease, out var pad)) return;
                work.Pad = pad;
            }
            var result = await _db.IssueVoyageCargoAsync(work.Job.User, work.Job.Profile, work.Job.Id, work.Lease, work.Recovery);
            work.Committed = true; _jobs[result.Id] = result;
            var destination = work.Pad is { } receiver && Exists(receiver) ? Transform(receiver).Coordinates : Transform(work.Console).Coordinates;
            foreach (var root in work.Roots)
            {
                if (_containers.TryGetContainingContainer((root, null, null), out var container))
                {
                    if (!_containers.Remove((root, null, null), container, force: true, destination: destination)) throw new InvalidOperationException("Груз ещё не выгружен");
                }
                else _transform.SetCoordinates(root, destination);
            }
            _recovery.AcceptExternalDelivery(work.Recovery, work.Entities);
            if (work.Pad is { } used) _cargo.ReleaseMarketTelepad(used, work.Lease, true);
            _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), work.Console);
            QueueDel(work.Escrow); _arrivals.Remove(work.Job.Id); _arrivalConsoles.Remove(work.Job.Id);
        }
        catch (InvalidOperationException) when (!work.Committed)
        {
            if (work.Pad is { } pad) _cargo.ReleaseMarketTelepad(pad, work.Lease, false);
            QueueDel(work.Escrow); _arrivals.Remove(work.Job.Id); throw;
        }
        finally { work.Busy = false; }
    }
}
