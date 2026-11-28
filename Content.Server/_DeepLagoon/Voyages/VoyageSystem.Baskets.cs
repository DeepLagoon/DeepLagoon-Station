using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.Containers;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Voyages;

public sealed partial class VoyageSystem
{
    private sealed record CartQuote(Guid Id, Guid User, int Profile, int Slot, EntityUid Console, Guid Cycle, TimeSpan Until,
        bool Imports, VoyageJob[] Jobs, VoyageBasketHire[] Hires, Order?[] Orders, VoyageCartQuoteView View);
    private sealed class BasketLaunch
    {
        public required CartQuote Quote;
        public required LaunchWork[] Works;
        public required VoyageBasketCommit Commit;
        public bool Busy;
    }
    private readonly Dictionary<Guid, CartQuote> _cartQuotes = new();
    private readonly Dictionary<Guid, BasketLaunch> _basketLaunches = new();
    private readonly Dictionary<(Guid User, int Profile), Guid> _lastCartCommitted = new();
    private TimeSpan _nextBaskets;

    private async Task CartRequest(VoyageRequest request, ICommonSession session, EntityUid console, EconomyAccount account, VoyageAccountState state)
    {
        if (session.AttachedEntity is not { } actor || !_purchaseAccess.IsAllowed(actor, console)) throw new InvalidOperationException("Нет доступа к заказам этой консоли");
        var user = session.UserId.UserId;
        if (request.Action != VoyageAction.ConfirmCart)
        {
            if (_basketLaunches.Values.Any(w => w.Quote.User == user)) throw new InvalidOperationException("Предыдущая корзина ещё подтверждается");
            var quote = QuoteCart(request, user, console, account, state);
            _cartQuotes[user] = quote;
            await Board(session, console, account.Profile, "", request.Destination, request.Search, request.Crew);
            return;
        }
        if (!_cartQuotes.TryGetValue(user, out var plan) || plan.Id != request.Quote || plan.Profile != account.Profile || plan.Slot != account.Slot ||
            plan.Console != console || plan.Cycle != _world.Cycle || plan.Until < _timing.RealTime)
            throw new InvalidOperationException("Расчёт корзины устарел. Рассчитайте заново");
        if (!_bank.BeginExchangePayment(session.UserId)) throw new InvalidOperationException("Другая банковская операция ещё не завершена");
        var works = new List<LaunchWork>();
        var transferred = false;
        try
        {
            // Stage every physical shipment and treasury reservation before any financial commit.
            var now = Now;
            for (var i = 0; i < plan.Jobs.Length; i++)
            {
                var job = plan.Jobs[i]; var duration = job.Arrive - job.Depart;
                job = job with { Depart = now, Arrive = now + duration, Return = now + duration * 2 };
                var order = plan.Orders[i];
                if (order != null && !Orders().Any(o => o.View.Id == order.View.Id)) throw new InvalidOperationException("Один из заказов уже недоступен");
                works.Add(PrepareLaunch(console, job, order, standalone: false));
            }
            var work = new BasketLaunch { Quote = plan, Works = works.ToArray(), Commit = new(plan.Id, user, account.Profile, account.Slot,
                works.Select(w => w.Job).ToArray(), plan.Hires, works.Select(w => w.Removal).ToArray()) };
            _basketLaunches.Add(plan.Id, work); transferred = true;
            var committed = await CommitBasket(work);
            await _bank.PublishExchangeBalance(session.UserId, account.Slot);
            await Board(session, console, account.Profile, committed ? "Корзина отправлена" : "Груз зарезервирован. Подтверждение отправки ожидается", request.Destination, request.Search);
        }
        catch
        {
            if (!transferred) foreach (var work in works) Rollback(work);
            throw;
        }
        finally { if (!transferred) _bank.EndExchangePayment(session.UserId); }
    }

    private CartQuote QuoteCart(VoyageRequest request, Guid user, EntityUid console, EconomyAccount account, VoyageAccountState state)
    {
        var imports = request.Action == VoyageAction.QuoteImportCart;
        if (request.Cart.Length is < 1 or > 32 || request.Cart.Any(l => l.Quantity is < 1 or > 5)) throw new InvalidOperationException("В корзине должно быть от 1 до 32 позиций");
        var groups = imports ? request.Cart.GroupBy(l => l.Sector).Select(g => g.ToArray()).ToArray() : request.Cart.Select(l => new[] { l }).ToArray();
        if (groups.Length > 8 || !imports && request.Cart.Select(l => l.Order).Distinct().Count() != groups.Length) throw new InvalidOperationException("Не более восьми разных рейсов в одной корзине");
        var occupied = _jobs.Values.Where(j => !j.Paid).Select(j => j.Crew).Concat(_launches.Values.Select(l => l.Job.Crew))
            .Concat(_basketLaunches.Values.SelectMany(w => w.Works.Select(l => l.Job.Crew))).ToHashSet();
        var free = new Queue<VoyageCrewRecord>(state.Crews.Where(c => c.Offer != "mercenary" && !occupied.Contains(c.Id)).OrderByDescending(c => c.Level));
        var offers = new Queue<VoyageMarketOffer>(_world.Offers.Where(o => o.Type != "mercenary").OrderBy(o => o.Cost).ThenByDescending(o => o.Level));
        var jobs = new List<VoyageJob>(); var hires = new List<VoyageBasketHire>(); var orders = new List<Order?>(); var views = new List<VoyageCartRouteView>();
        long expected = 0, goodsTotal = 0;
        var reservedGoods = new Dictionary<EntityUid, int>();
        var origin = _stations.GetOwningStation(console) is { } station ? Name(station) : Name(console);
        foreach (var group in groups)
        {
            var sector = group[0].Sector;
            var node = _galaxy.Systems.FirstOrDefault(n => n.Id == sector && n.Known && n.Id != VoyageRouteModel.Home)
                ?? throw new InvalidOperationException("Выберите изученный торговый сектор");
            VoyageCrewRecord crew; var hired = false;
            if (free.Count > 0) crew = free.Dequeue();
            else
            {
                if (offers.Count == 0) throw new InvalidOperationException("Недостаточно предложений найма транспортных экипажей");
                var offer = offers.Dequeue();
                crew = new(Guid.NewGuid(), user, account.Profile, account.Slot, offer.Type, offer.Level,
                    offer.Level * (offer.Level - 1) * 30, false, offer.Cost);
                hires.Add(new(crew, offer.Id, offer.Cost)); hired = true;
            }
            var crewType = VoyageRouteModel.CrewOffers.Single(o => o.Id == crew.Offer);
            var path = _galaxy.Path(VoyageRouteModel.Home, sector);
            if (path.Length < 2) throw new InvalidOperationException("Нет торгового маршрута");
            var duration = Math.Max(1000L, (long)Math.Ceiling(_galaxy.Minutes(path) * 60000d * Math.Clamp(_config.GetCVar(VoyageCVars.TravelTimeScale), .001f, 10) /
                (crewType.Speed * (1 + .04 * (crew.Level - 1)))));
            var risk = Risk(path, crew.Level, false);
            var replacement = crew.HirePrice > 0 ? crew.HirePrice : crewType.Cost;
            var crewAllowance = (hired ? replacement : replacement / 5d) + risk * replacement;
            long cost = 0, reward = 0, goods = 0;
            string manifest; VoyageCargoLine[]? cargo = null; Order? order = null;
            if (imports)
            {
                var lines = new List<VoyageCargoLine>();
                foreach (var entries in group.GroupBy(l => l.Product))
                {
                    var quantity = entries.Sum(l => l.Quantity);
                    if (quantity > 5) throw new InvalidOperationException("Не более пяти упаковок одной позиции из сектора");
                    var product = Products(console).FirstOrDefault(p => p.ID == entries.Key) ?? throw new InvalidOperationException("Товар больше недоступен");
                    if (!Supply(console, node, product.Name).Any(p => p.Id == product.ID)) throw new InvalidOperationException("В выбранном секторе нет этого товара");
                    var price = SupplyPrice(product, sector);
                    lines.Add(new(product.Product.Id, product.Name, quantity, price));
                    cost = checked(cost + price * quantity);
                    goods = checked(goods + EstimateImportValue(product.Product.Id) * quantity);
                }
                cargo = lines.ToArray();
                if (cargo.Sum(l => l.Quantity) > 40) throw new InvalidOperationException("Не более сорока упаковок одним рейсом");
                manifest = string.Join(", ", cargo.Select(l => l.Name + " ×" + l.Quantity));
                expected += (long)Math.Floor((1 - risk) * goods - cost - crewAllowance);
            }
            else
            {
                order = Orders().FirstOrDefault(o => o.View.Id == group[0].Order && o.View.Destination == sector) ?? throw new InvalidOperationException("Заказ уже недоступен");
                if (!_cargo.PlanVoyageCargo(console, order.Prototype, out var materials, reservedGoods)) throw new InvalidOperationException("На поддонах недостаточно груза для «" + order.View.Manifest + "»");
                foreach (var (item, units) in materials) reservedGoods[item] = reservedGoods.GetValueOrDefault(item) + units;
                var pricing = EntityManager.System<PricingSystem>();
                var appraisal = materials.Sum(p => pricing.GetPrice(p.Key) * p.Value /
                    (TryComp<StackComponent>(p.Key, out var stack) ? Math.Max(1, stack.Count) : 1));
                goods = (long)Math.Clamp(Math.Ceiling(double.IsFinite(appraisal) ? appraisal : 0), 0, int.MaxValue);
                var margin = Math.Max(1000, goods * .15 + duration / 60000d * 25);
                reward = Math.Max(order.View.Reward, (long)Math.Ceiling((goods + crewAllowance + margin) / Math.Max(.05, 1 - risk)));
                if (reward > int.MaxValue) throw new InvalidOperationException("Заказ превышает допустимый бюджет рейса");
                manifest = order.View.Manifest;
                expected += (long)Math.Floor((1 - risk) * reward - goods - crewAllowance);
            }
            goodsTotal = checked(goodsTotal + goods);
            var now = Now;
            jobs.Add(new(Guid.NewGuid(), user, account.Profile, account.Slot, crew.Id, order?.View.Id ?? "", sector, origin, path, manifest,
                reward, (byte)(order?.Payer ?? Content.Shared._NF.Bank.Components.SectorBankAccount.Invalid), now, now + duration, now + duration * 2, false,
                Cycle: _world.Cycle, Kind: imports ? 2 : 0, Product: cargo?.FirstOrDefault()?.Prototype ?? "", Quantity: cargo?.Sum(l => l.Quantity) ?? 0,
                PurchaseCost: cost, LegRisk: _world.Risk(path, now, crew.Level, false), Cargo: cargo));
            orders.Add(order);
            views.Add(new(sector, node.Name, manifest, crewType.Name, crew.Level, duration * 2 / 1000d, risk, cost, reward, goods));
        }
        if (state.Crews.Length + hires.Count > 16) throw new InvalidOperationException("Достигнут предел экипажей: 16");
        var total = checked(hires.Sum(h => h.Cost) + jobs.Sum(j => j.PurchaseCost));
        if (account.Bank < total) throw new InvalidOperationException("Недостаточно денег на корзину и недостающие экипажи");
        var id = request.Operation == Guid.Empty ? Guid.NewGuid() : request.Operation;
        var view = new VoyageCartQuoteView(id, imports, hires.Count, hires.Sum(h => h.Cost), jobs.Sum(j => j.PurchaseCost),
            jobs.Sum(j => j.Reward), goodsTotal, expected, views.ToArray());
        return new(id, user, account.Profile, account.Slot, console, _world.Cycle, _timing.RealTime + TimeSpan.FromSeconds(45), imports,
            jobs.ToArray(), hires.ToArray(), orders.ToArray(), view);
    }

    private long EstimateImportValue(string prototype, int depth = 0)
    {
        if (depth > 5 || !_prototypes.TryIndex<EntityPrototype>(prototype, out var definition) || definition.TryGetComponent<CargoSellBlacklistComponent>(out _)) return 0;
        var value = EntityManager.System<PricingSystem>().GetEstimatedPrice(definition);
        if (definition.TryGetComponent<ContainerFillComponent>(out var containers))
            value += containers.Containers.Values.SelectMany(c => c).Sum(p => EstimateImportValue(p.Id, depth + 1));
        if (definition.TryGetComponent<StorageFillComponent>(out var storage))
            foreach (var entry in storage.Contents.Where(e => e.PrototypeId != null && e.GroupId == null))
                value += EstimateImportValue(entry.PrototypeId!.Value.Id, depth + 1) * entry.SpawnProbability * Math.Max(entry.Amount, (entry.Amount + entry.MaxAmount) / 2d);
        return (long)Math.Clamp(Math.Ceiling(double.IsFinite(value) ? value : 0), 0, int.MaxValue);
    }

    private async Task<bool> CommitBasket(BasketLaunch work)
    {
        if (work.Busy) return false;
        work.Busy = true;
        var committed = false;
        try
        {
            await _db.CommitVoyageBasketAsync(work.Commit);
            committed = true;
            foreach (var launch in work.Works)
            {
                _jobs.TryAdd(launch.Job.Id, launch.Job);
                if (launch.Order is { } order) _bounties[launch.Job.Id] = (order.Station, order.Data);
                if (launch.Job.Kind == 2) _arrivalConsoles[launch.Job.Id] = work.Quote.Console;
                QueueDel(launch.Escrow);
            }
            _lastCartCommitted[(work.Quote.User, work.Quote.Profile)] = work.Quote.Id;
            _cartQuotes.Remove(work.Quote.User); _basketLaunches.Remove(work.Quote.Id);
            _bank.EndExchangePayment(new(work.Quote.User));
            await RetryWorld();
            if (Exists(work.Quote.Console)) _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), work.Quote.Console);
            return true;
        }
        catch (InvalidOperationException) when (!committed)
        {
            _basketLaunches.Remove(work.Quote.Id);
            foreach (var launch in work.Works) Rollback(launch);
            _bank.EndExchangePayment(new(work.Quote.User));
            throw;
        }
        catch (Exception e) { Log.Warning($"Voyage basket {work.Quote.Id} awaits its durable receipt: {e.Message}"); return false; }
        finally { work.Busy = false; }
    }
    private async Task RetryBasket(BasketLaunch work)
    {
        try { await CommitBasket(work); }
        catch (Exception e) { Log.Warning($"Voyage basket {work.Quote.Id} cancelled: {e.Message}"); }
    }
}
