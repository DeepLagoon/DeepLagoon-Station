using Content.Server.Shuttles.Components;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Mono.FireControl;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio;
using Robust.Shared.Player;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class SectorExpeditionSystem
{
    [Dependency] private ISharedPlayerManager _players = default!;
    private readonly Dictionary<Guid, Trip> _hireSettlements = new();
    private readonly HashSet<Guid> _settlingHires = new();
    private int CrewCount(EntityUid ship) => EntityQuery<MindContainerComponent, TransformComponent>().Count(e => e.Item1.HasMind && e.Item2.GridUid == ship);
    private int WeaponCount(EntityUid ship) => EntityQuery<FireControllableComponent, TransformComponent>().Count(e => e.Item2.GridUid == ship);
    private EntityUid? ServiceShip(SectorCrewService service)
    {
        var query = EntityQueryEnumerator<ShuttleComponent, TransformComponent>();
        while (query.MoveNext(out var ship, out _, out _))
        {
            var identity = TryComp<ParkingShuttleComponent>(ship, out var parking) ? parking.Identity : TryComp<SectorCrewIdentityComponent>(ship, out var generic) ? generic.Identity : Guid.Empty;
            if (identity == service.Vessel && !(parking?.Parked ?? false)) return ship;
        }
        return null;
    }
    private bool ServiceAvailable(SectorCrewService service)
    {
        if (!service.Listed || !_players.TryGetSessionById(new(service.User), out var captain) ||
            !_bank.IsCharacter(captain, service.Slot) ||
            captain.AttachedEntity is not { } body || !TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive) return false;
        var ship = ServiceShip(service);
        return ship != null && Transform(body).GridUid == ship && !_trips.ContainsKey(ship.Value) && !HasComp<FTLComponent>(ship.Value);
    }
    private async Task MarketAction(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, SectorExpeditionRequest request)
    {
        var user = session.UserId.UserId; var cycle = _voyages.WorldSnapshot.Cycle;
        if (request.Action == SectorExpeditionAction.PostResearch)
        {
            if (SectorResearchDefinition.Settled(request.Sector)) throw new InvalidOperationException("Этот сектор уже исследован");
            if ((_voyages.WorldSnapshot.Research.GetValueOrDefault(request.Sector)?.Completed.Count ?? 0) >= SectorResearchDefinition.Count(request.Sector, _voyages.WorldSnapshot.Seed))
                throw new InvalidOperationException("Исследование этого сектора завершено");
            var node = _voyages.RouteGraph.Systems.FirstOrDefault(s => s.Id == request.Sector && s.Id != VoyageRouteModel.Home) ?? throw new InvalidOperationException("Выберите сектор для заказа");
            var name = account.Character;
            await _db.PublishSectorResearchAsync(new(request.Operation, cycle, node.Id, name, request.Price, request.Price, user, account.Profile, account.Slot, 0,
                SectorResearchDefinition.Count(node.Id, _voyages.WorldSnapshot.Seed), new()));
        }
        else if (request.Action == SectorExpeditionAction.CancelResearch) await _db.CancelSectorResearchAsync(request.Order, user, account.Profile);
        else if (request.Action is SectorExpeditionAction.PublishCrew or SectorExpeditionAction.WithdrawCrew)
        {
            if (!OwnsShip(session, ship)) throw new InvalidOperationException("Опубликовать экипаж может владелец шаттла");
            var name = account.Character;
            var board = await _db.ReadSectorResearchAsync(cycle, user, account.Profile);
            var old = board.Crews.FirstOrDefault(c => c.Id == ServiceIdentity(ship, user, account.Profile));
            var service = new SectorCrewService(ServiceIdentity(ship, user, account.Profile), user, account.Profile, account.Slot, name, Name(ship), request.Text.Trim(),
                Math.Clamp(CrewCount(ship), 1, 64), WeaponCount(ship), request.Action == SectorExpeditionAction.WithdrawCrew ? old?.Price ?? 100 : request.Price,
                Listed: request.Action == SectorExpeditionAction.PublishCrew, Vessel: VesselIdentity(ship));
            await _db.PublishSectorCrewAsync(service);
        }
        else if (request.Action == SectorExpeditionAction.HireCrew)
        {
            var board = await _db.ReadSectorResearchAsync(cycle, user, account.Profile);
            var service = board.Crews.FirstOrDefault(c => c.Id == request.Offer) ?? throw new InvalidOperationException("Предложение больше не опубликовано");
            if (!ServiceAvailable(service)) throw new InvalidOperationException("Капитан или шаттл сейчас недоступны");
            var name = account.Character;
            await _db.HireSectorCrewAsync(request.Operation, service.Id, user, account.Profile, name, request.Sector == VoyageRouteModel.Home ? "" : request.Sector, request.Price);
        }
        else
        {
            var action = request.Action switch
            {
                SectorExpeditionAction.AcceptHire => "accept", SectorExpeditionAction.DeclineHire => "decline", SectorExpeditionAction.CancelHire => "cancel",
                SectorExpeditionAction.FinishHire => "finish", SectorExpeditionAction.ConfirmHire => "confirm", _ => throw new InvalidOperationException("Неизвестное действие"),
            };
            var hire = await _db.SectorHireActionAsync(request.Order, user, account.Profile, action);
            await _bank.PublishExchangeBalance(new(hire.Buyer), hire.BuyerSlot); await _bank.PublishExchangeBalance(new(hire.Provider), hire.ProviderSlot);
        }
        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg"), console);
    }
    private async Task SettleHumanHire(Trip trip)
    {
        try
        {
            if (trip.Hire != Guid.Empty)
            {
                var action = trip.Wiped ? "fail" : trip.CompletedWork > 0 ? "auto" : "finish";
                var board = await _db.ReadSectorResearchAsync(trip.Cycle, trip.User, trip.Profile);
                var current = board.Hires.FirstOrDefault(h => h.Id == trip.Hire);
                var hire = current is { Status: 2 } && action == "finish" ? current : await _db.SectorHireActionAsync(trip.Hire, trip.User, trip.Profile, action);
                await _bank.PublishExchangeBalance(new(hire.Buyer), hire.BuyerSlot); await _bank.PublishExchangeBalance(new(hire.Provider), hire.ProviderSlot);
            }
            _hireSettlements.Remove(trip.Id);
        }
        catch (Exception e) { Log.Warning($"Human crew settlement {trip.Id} deferred: {e.Message}"); }
        finally { _settlingHires.Remove(trip.Id); }
    }
    public static bool DebugResearchFunding
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    private readonly Dictionary<Guid, SectorResearchOrder> _pendingAutoOrders = new();
    private int _researchSettingsVersion;
    private bool _autoOrdersBusy;
    private TimeSpan _nextAutoOrders;
    private async Task EnsureAutomaticOrders()
    {
        _autoOrdersBusy = true;
        var settingsVersion = _researchSettingsVersion;
        // Retry a database failure promptly; successful reconciliation supplies the durable 15-minute deadline.
        _nextAutoOrders = _timing.RealTime + TimeSpan.FromSeconds(10);
        try
        {
            var world = _voyages.WorldSnapshot;
            var graph = _voyages.RouteGraph;
            var seconds = Math.Clamp(_config.GetCVar(SectorResearchSettings.RefreshSeconds), 1, 86400);
            var budget = Math.Clamp(_config.GetCVar(SectorResearchSettings.Budget), 0, 1000000);
            var visible = VoyageVisibility.VisibleSystems(graph);
            var candidates = graph.Systems.Where(s => visible.Contains(s.Id) && !SectorResearchDefinition.Settled(s.Id) &&
                (world.Research.GetValueOrDefault(s.Id)?.Completed.Count ?? 0) < SectorResearchDefinition.Count(s.Id, world.Seed) &&
                (s.Known ? graph.Path(VoyageRouteModel.Home, s.Id).Length > 1 : graph.SurveyPath(s.Id).Length > 1))
                .Select(s => new AutomaticSectorResearchTarget(s.Id, SectorResearchDefinition.Count(s.Id, world.Seed), (long)(budget > 0 ? Math.Max(100, budget) : SectorResearchDefinition.Pool(s.Id, world.Seed)))).ToArray();
            if (DebugResearchFunding)
            {
                var result = await _db.RefreshAutomaticSectorResearchAsync(world.Cycle, candidates, Now, seconds * 1000L);
                _nextAutoOrders = _timing.RealTime + TimeSpan.FromMilliseconds(Math.Clamp(result.NextRefresh - Now, 1000, seconds * 1000L));
                if (result.Created > 0) Log.Info($"Debug research fund published {result.Created} available-sector orders; refresh interval={seconds}s.");
            }
            else
            {
                await PublishFactionResearch(world, candidates);
                _nextAutoOrders = _timing.RealTime + TimeSpan.FromSeconds(_pendingAutoOrders.Count > 0 ? Math.Min(seconds, 10) : seconds);
            }
        }
        catch (Exception e) { Log.Warning($"Automatic research publication deferred: {e.Message}"); }
        finally { _autoOrdersBusy = false; if (settingsVersion != _researchSettingsVersion) _nextAutoOrders = TimeSpan.Zero; }
    }
    private async Task PublishFactionResearch(VoyageWorld world, AutomaticSectorResearchTarget[] candidates)
    {
        var board = await _db.ReadSectorResearchAsync(world.Cycle, Guid.Empty, 0);
        var factions = new[] { SectorBankAccount.Frontier, SectorBankAccount.Medical, SectorBankAccount.Nfsd, SectorBankAccount.Mieyo };
        foreach (var target in candidates)
        {
            if (board.Orders.Any(o => o.User == Guid.Empty && !o.DebugFunded && o.Sector == target.Sector) || _pendingAutoOrders.Values.Any(o => o.Sector == target.Sector)) continue;
            var completed = world.Research.GetValueOrDefault(target.Sector)?.Completed.Count ?? 0;
            var price = (target.Reward * (target.Planets - completed) + target.Planets - 1) / target.Planets;
            if (price <= 0) continue;
            var offset = (int)(VoyageGalaxy.Hash(target.Sector, world.Seed) % (uint)factions.Length);
            foreach (var faction in factions.Skip(offset).Concat(factions.Take(offset)))
            {
                if (!_bank.TryGetBalance(faction, out var balance) || balance < price || !_bank.TrySectorWithdraw(faction, price, LedgerEntryType.StationWithdrawalBounty)) continue;
                var order = new SectorResearchOrder(Guid.NewGuid(), world.Cycle, target.Sector, Loc.GetString("ledger-tab-" + faction), price, price,
                    Guid.Empty, 0, 0, (byte)faction, target.Planets, new(), Automatic: true);
                _pendingAutoOrders[order.Id] = order; break;
            }
        }
        foreach (var order in _pendingAutoOrders.Values.ToArray())
        {
            try { await _db.PublishSectorResearchAsync(order); _pendingAutoOrders.Remove(order.Id); }
            catch (InvalidOperationException)
            {
                if (world.Cycle == _voyages.WorldSnapshot.Cycle && !_voyages.WorldSnapshot.Closed)
                    _bank.TrySectorDeposit((SectorBankAccount)order.Faction, order.Total, LedgerEntryType.StationDepositOther);
                _pendingAutoOrders.Remove(order.Id);
            }
        }
    }
    public VoyageFlightView[] HumanFlights(Guid viewer, int profile) => _trips.Values.Where(t => !t.LocalPlanet && t.Phase is (Phase.Departing or Phase.ReturningHome or Phase.EmergencyHome))
        .Select(t => new VoyageFlightView(t.Id, t.User == viewer && t.Profile == profile, Guid.Empty, t.Name, t.Path,
            t.Depart / 1000d, t.Phase == Phase.Departing ? t.Arrive / 1000d : t.Depart / 1000d, t.Phase == Phase.Departing ? t.Arrive / 1000d + .001 : t.Arrive / 1000d,
            t.Phase != Phase.Departing, false, false, "Экипаж игрока", Kind: 4)).ToArray();
    private async Task Board(ICommonSession session, EntityUid console, EconomyAccount account, string selected, string status, bool focusSelected = false)
    {
        var world = _voyages.WorldSnapshot; var graph = _voyages.RouteGraph;
        var visible = VoyageVisibility.VisibleSystems(graph);
        if (!graph.Systems.Any(s => s.Id == selected) || !visible.Contains(selected)) selected = VoyageRouteModel.Home;
        var ship = GetShip(console); _trips.TryGetValue(ship, out var trip);
        var board = await _db.ReadSectorResearchAsync(world.Cycle, session.UserId.UserId, account.Profile);
        if (!CanUse(session, console)) return;
        var path = graph.System(selected).Known ? graph.Path(trip?.Sector ?? VoyageRouteModel.Home, selected) : graph.SurveyPath(selected);
        _researchClaims.TryGetValue((session.UserId.UserId, account.Profile), out var claim);
        var location = PlayerLocation(session.AttachedEntity);
        var departureBlock = DepartureBlockReason(session, console, ship, account, selected, path);
        if (departureBlock.Length == 0 && !board.Orders.Any(o => o.Sector == selected)) departureBlock = "В секторе нет действующей заявки на исследование";
        var flightSeconds = path.Length > 1 ? JourneySeconds(ship, path) : 0;
        var planets = new List<SectorPlanetView>();
        if (trip != null && _orbits.TryGetValue(trip.Sector, out var orbit))
            foreach (var (index, marker) in orbit.Markers)
            {
                var position = PlanetPosition(trip.Sector, index);
                var progress = world.Research.GetValueOrDefault(trip.Sector);
                var distance = Transform(ship).MapUid == orbit.Map ? System.Numerics.Vector2.Distance(_transform.GetWorldPosition(ship), position) : 0;
                var occupied = progress?.Leases.TryGetValue(index, out var lease) == true && lease.Run == _run && lease.Trip != trip.Id;
                var done = progress?.Completed.Contains(index) == true;
                planets.Add(new(index, Name(marker), PlanetType(trip.Sector, index), position.X, position.Y,
                    SectorResearchDefinition.Difficulty(trip.Sector, index, world.Seed), done, occupied, distance,
                    trip.Phase == Phase.Orbit && ResearchLandingAllowed(trip, index) && !_encounterBusy.Contains(trip.Sector) && !orbit.Pirates.Any(p => !p.Counted) && !HasComp<FTLComponent>(ship) && !_planetLaunching.Contains((trip.Sector, index)) && (!_planetMaps.TryGetValue((trip.Sector, index), out var activeMap) ? !occupied : CanJoinPlanet(activeMap)) && distance <= _config.GetCVar(SectorExpeditionCVars.LandRange)));
            }
        var local = trip == null || trip.LocalPlanet;
        if (local && (trip?.HomeMap ?? Transform(ship).MapUid) is { } homeMap) planets.AddRange(HomePlanetViews(ship, trip, homeMap));
        for (var i = 0; i < planets.Count; i++)
        {
            var reason = !CanPilot(session, console) ? "Нет доступа к управлению экспедицией" :
                trip != null && (trip.User != session.UserId.UserId || trip.Profile != account.Profile) ? "Посадка доступна капитану рейса" :
                PlanetBlockReason(ship, trip, planets[i]);
            planets[i] = planets[i] with { CanLand = planets[i].CanLand && reason.Length == 0, LandingBlockReason = reason };
        }
        var positionScale = local ? HomePlanetMapScale : 1;
        var mission = trip?.PlanetMap is { } map && TryComp<Content.Server.Salvage.Expeditions.SalvageExpeditionComponent>(map, out var expedition) ? expedition : null;
        if (status.Length == 0 && trip is { Phase: Phase.Orbit } && _orbits.TryGetValue(trip.Sector, out var currentOrbit))
            status = _encounterBusy.Contains(trip.Sector) ? "Определение обстановки" : currentOrbit.Pirates.Count(p => !p.Counted) is var hostile && hostile > 0 ? "Пиратские корабли: " + hostile : "";
        var views = board.Crews.Select(c => new SectorCrewServiceView(c.Id, c.Captain, c.Ship, c.Description, c.Members, c.Weapons, c.Price, c.Level, c.Experience,
            c.User == session.UserId.UserId && c.Profile == account.Profile, ServiceAvailable(c) && !board.BusyOffers.Contains(c.Id), c.Listed)).ToArray();
        RaiseNetworkEvent(new SectorExpeditionSnapshot
        {
            Console = GetNetEntity(console), Status = status, Selected = selected, FocusSelected = focusSelected, CurrentSector = trip?.Sector ?? VoyageRouteModel.Home,
            Home = local, ActiveOrder = claim?.Order ?? Guid.Empty, ClaimedSector = claim?.Sector ?? "", InFlight = trip?.Phase is Phase.Departing or Phase.LeavingPlanet or Phase.ReturningHome or Phase.EmergencyHome or Phase.GeneratingPlanet,
            OnPlanet = trip?.Phase == Phase.Planet, CanReturn = claim is { Returning: false } || trip is { Phase: Phase.Orbit or Phase.Planet } && !HasComp<FTLComponent>(ship),
            CanDepart = departureBlock.Length == 0 && flightSeconds > 0, DepartureBlockReason = departureBlock,
            WithoutFtlDrive = !_shuttles.TryGetFTLDrive(ship, out _, out _),
            FlightSeconds = flightSeconds, Planets = planets.ToArray(), SuggestedPool = SectorResearchDefinition.Pool(selected, world.Seed),
            ShipX = _transform.GetWorldPosition(ship).X * positionScale, ShipY = _transform.GetWorldPosition(ship).Y * positionScale,
            MissionDone = mission?.ObjectivesCompleted ?? 0, MissionTotal = mission?.ObjectivesTotal ?? 0, ActivePlanet = trip?.Planet ?? -1,
            Progress = graph.Systems.ToDictionary(s => s.Id, s => SectorResearchDefinition.Settled(s.Id) ? 100 : (int)((world.Research.GetValueOrDefault(s.Id)?.Completed.Count ?? 0) * 100d / SectorResearchDefinition.Count(s.Id, world.Seed))),
            Orders = board.Orders.Where(o => visible.Contains(o.Sector)).Select(o => new SectorResearchView(o.Id, o.Sector, o.Buyer, o.Total, o.Remaining, o.User == session.UserId.UserId && o.Profile == account.Profile,
                world.Research.GetValueOrDefault(o.Sector)?.Completed.Count ?? 0, o.Planets)).ToArray(),
            Crews = views, Hires = board.Hires.Select(h => new SectorCrewHireView(h.Id, h.Captain, h.Employer, h.Ship, h.Price, h.Sector, h.Status,
                h.Provider == session.UserId.UserId && h.ProviderProfile == account.Profile, h.Buyer == session.UserId.UserId && h.BuyerProfile == account.Profile)).ToArray(),
            Map = new VoyageSnapshot { Console = GetNetEntity(console), Origin = "Колосс-Централ", Cycle = world.Cycle, Seed = world.Seed, Clock = Now / 1000d,
                Bank = account.Bank, PlayerSector = location.Sector, PlayerFlight = location.Flight, Known = world.Known.ToArray(), Piracy = world.Routes.ToDictionary(p => p.Key, p => p.Value.Value(Now)),
                Flights = _voyages.PublicFlights(session.UserId.UserId, account.Profile), Orders = _voyages.SectorOrderViews(),
                Products = SectorResearchDefinition.Settled(selected) && selected != VoyageRouteModel.Home ? _voyages.SectorProducts(console, selected).Take(12).ToArray() : [] },
        }, session);
    }
    private string PlanetType(string sector, int index)
    {
        string[] types = ["PlanetTypeHabitable", "PlanetTypeBarren", "PlanetTypeDesert", "PlanetTypeIce", "PlanetTypeVolcanic", "PlanetTypeGasGiant"];
        return types[SectorResearchDefinition.PlanetSeed(sector, index, _voyages.WorldSnapshot.Seed) % types.Length];
    }
}
