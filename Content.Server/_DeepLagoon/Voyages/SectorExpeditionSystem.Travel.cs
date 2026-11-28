using Content.Server._NF.Shuttles.Components;
using Content.Shared.Timing;
using Content.Shared.Salvage;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;
namespace Content.Server._DeepLagoon.Voyages;
public sealed partial class SectorExpeditionSystem
{
    private sealed record Visit(Guid Trip, Guid Cycle, string Sector, int Planet, Guid User, int Profile, int Slot, Guid Service, EntityUid Ship, bool Failed = false, string SessionKey = "", bool Local = false);
    private readonly Dictionary<EntityUid, List<Visit>> _visits = new();
    private readonly Dictionary<EntityUid, (Visit[] Visits, bool Complete)> _completions = new();
    private readonly HashSet<EntityUid> _settling = new();
    private readonly HashSet<(EntityUid Map, Guid Trip)> _workProof = new();
    private float JourneySeconds(EntityUid ship, string[] path)
    {
        if (!_shuttles.TryGetFTLDrive(ship, out var driveEntity, out var drive))
            return SectorResearchDefinition.FlightTime(_voyages.RouteGraph.Minutes(path), 5.5f, 20f, hasDrive: false);
        if (!_power.IsPowered(driveEntity.Value)) return 0;
        return SectorResearchDefinition.FlightTime(_voyages.RouteGraph.Minutes(path), drive.StartupTime, drive.HyperSpaceTime);
    }
    private void ValidateShip(EntityUid ship)
    {
        if (HasComp<FTLComponent>(ship)) throw new InvalidOperationException("Дождитесь окончания БСС и перезарядки двигателя");
        if (HasComp<ForceAnchorComponent>(ship) || _salvage.IsExpeditionShuttleDocked(ship)) throw new InvalidOperationException("Открепите шаттл от станции и других кораблей");
        if (_shuttles.TryGetFTLDrive(ship, out var driveEntity, out _) && !_power.IsPowered(driveEntity.Value))
            throw new InvalidOperationException("БСС-двигатель не получает питание");
        if (!HasComp<SalvageExpeditionComponent>(Transform(ship).MapUid) && !_shuttles.CanFTL(ship, out var reason)) throw new InvalidOperationException(reason);
    }
    private string DepartureBlockReason(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, string target, string[] path)
    {
        if (SectorResearchDefinition.Settled(target)) return target == VoyageRouteModel.Home ? "Родной сектор" : "Технический сектор используется только для транзита";
        if (_researchClaims.TryGetValue((session.UserId.UserId, account.Profile), out var claim) &&
            (claim.Sector != target || claim.Vessel != VesselIdentity(ship) || claim.Returning)) return "Сначала завершите текущий заказ и вернитесь домой либо откажитесь от него";
        if (path.Length < 2) return "Нет доступного маршрута в выбранный сектор";
        if (!CanPilot(session, console)) return "Нет доступа к управлению экспедицией";
        if ((HasComp<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(ship) || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(ship)) && !OwnsShip(session, ship))
            return "Исследовательский рейс должен начать владелец шаттла";
        if (_trips.TryGetValue(ship, out var trip))
        {
            if (trip.User != session.UserId.UserId || trip.Profile != account.Profile) return "Рейс уже начат другим капитаном";
            if (trip.Phase != Phase.Orbit) return trip.Phase == Phase.Planet ? "Сначала покиньте планету" : "Дождитесь окончания текущего перелёта";
            if (!_voyages.RouteGraph.System(target).Known) return "Новый маршрут исследуется из родного сектора";
        }
        try { ValidateShip(ship); }
        catch (InvalidOperationException error) { return error.Message; }
        return "";
    }
    private async Task Depart(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, SectorExpeditionRequest request)
    {
        ValidateShip(ship);
        if ((HasComp<Content.Shared._NF.Shipyard.Components.ShipOwnershipComponent>(ship) || HasComp<Content.Server._DeepLagoon.ShuttleParking.ParkingShuttleComponent>(ship)) && !OwnsShip(session, ship))
            throw new InvalidOperationException("Исследовательский рейс должен начать владелец шаттла");
        var graph = _voyages.RouteGraph;
        if (SectorResearchDefinition.Settled(request.Sector)) throw new InvalidOperationException("Технический сектор используется только для транзита");
        if (!_researchClaims.TryGetValue((session.UserId.UserId, account.Profile), out var claim) || claim.Order != request.Order || claim.Sector != request.Sector ||
            claim.Cycle != _voyages.WorldSnapshot.Cycle || claim.Vessel != VesselIdentity(ship) || claim.Returning)
            throw new InvalidOperationException("Сначала возьмите действующую заявку на выбранный сектор");
        if (_trips.ContainsKey(ship)) throw new InvalidOperationException("Новый рейс можно начать после завершения текущего заказа и возвращения домой");
        var target = graph.Systems.FirstOrDefault(s => s.Id == request.Sector && s.Id != VoyageRouteModel.Home) ?? throw new InvalidOperationException("Выберите исследуемый сектор");
        if (_trips.TryGetValue(ship, out var existing) && existing.Phase != Phase.Orbit) throw new InvalidOperationException("Сначала вернитесь с планеты в сектор");
        if (existing != null && (existing.User != session.UserId.UserId || existing.Profile != account.Profile)) throw new InvalidOperationException("Рейс уже начат другим капитаном");
        var start = existing?.Sector ?? VoyageRouteModel.Home;
        var path = target.Known ? graph.Path(start, target.Id) : graph.SurveyPath(target.Id);
        if (start != VoyageRouteModel.Home && !target.Known) throw new InvalidOperationException("Новый маршрут исследуется из родного сектора");
        if (path.Length < 2) throw new InvalidOperationException("К сектору нет доступного маршрута");
        var board = await _db.ReadSectorResearchAsync(_voyages.WorldSnapshot.Cycle, session.UserId.UserId, account.Profile);
        var order = board.Orders.FirstOrDefault(o => o.Id == request.Order && o.Sector == target.Id);
        if (order == null) throw new InvalidOperationException("Выберите действующий заказ на исследование этого сектора");
        if (!_voyages.Ready || !CanUse(session, console)) return;
        ValidateShip(ship);
        var transform = Transform(ship);
        var trip = existing ?? new Trip { Id = Guid.NewGuid(), User = session.UserId.UserId, Profile = account.Profile, Slot = account.Slot,
            Cycle = _voyages.WorldSnapshot.Cycle, Ship = ship, Console = console, HomeMap = transform.MapUid!.Value,
            Home = new EntityCoordinates(transform.MapUid.Value, _transform.GetWorldPosition(transform)), HomeAngle = _transform.GetWorldRotation(transform),
            Sector = target.Id, Name = Name(ship) };
        trip.HomePath = existing == null ? path : [..trip.HomePath, ..path.Skip(1)];
        trip.Console = console; trip.Sector = target.Id; trip.Path = path; trip.Phase = Phase.Departing;
        trip.Service = ServiceIdentity(ship, trip.User, trip.Profile);
        if (OwnsShip(session, ship) && !board.Crews.Any(c => c.Id == trip.Service))
        {
            var captain = account.Character;
            await _db.PublishSectorCrewAsync(new(trip.Service, trip.User, trip.Profile, trip.Slot, captain, Name(ship), "", Math.Clamp(CrewCount(ship), 1, 64), WeaponCount(ship), 15000,
                Listed: false, Vessel: VesselIdentity(ship)));
            if (!CanUse(session, console)) return; ValidateShip(ship);
        }
        var hire = board.Hires.FirstOrDefault(h => h.Offer == trip.Service && h.Status == 1 && (h.Sector.Length == 0 || h.Sector == target.Id)); trip.Hire = hire?.Id ?? Guid.Empty;
#if DEBUG
        ArmPirateTest(ship, order);
#endif
        var orbit = GetOrbit(target.Id);
        CaptureCrew(trip); if (trip.Bodies.Count == 0 && session.AttachedEntity is { } actor) trip.Bodies.Add(actor);
        claim.Started = true;
        _trips[ship] = trip; EnsureComp<SectorExpeditionShipComponent>(ship).Trip = trip.Id;
        var seconds = JourneySeconds(ship, path);
        trip.Depart = Now; trip.Arrive = Now + (long)(seconds * 1000);
        try { Jump(ship, new EntityCoordinates(orbit.Map, Vector2.Zero), Angle.Zero, seconds); }
        catch
        {
            if (existing == null) { _trips.Remove(ship); claim.Started = false; RemComp<SectorExpeditionShipComponent>(ship); }
            else trip.Phase = Phase.Orbit;
            throw;
        }
        Log.Info($"Sector trip {trip.Id}: ship={ship}, profile={trip.Profile}, route={string.Join(">", path)}, duration_s={seconds:0.00}");
    }
    private void CaptureCrew(Trip trip)
    {
        var query = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var body, out var mind, out _, out var position))
            if (mind.HasMind && position.GridUid == trip.Ship && !HasComp<Content.Shared.NPC.ActiveNPCComponent>(body)) trip.Bodies.Add(body);
    }
    private void Jump(EntityUid ship, EntityCoordinates coordinates, Angle angle, float seconds)
    {
        var component = Comp<ShuttleComponent>(ship);
        const float arrival = 1.5f; var startup = Math.Min(2.5f, Math.Max(1, seconds * .2f));
        _shuttles.FTLToCoordinates(ship, component, coordinates, angle, startup, seconds - startup, safeExpeditionReturn: true);
        if (!TryComp<FTLComponent>(ship, out var ftl)) throw new InvalidOperationException("БСС не запущено");
        ftl.StartupTime = startup; ftl.StateTime = StartEndTime.FromCurTime(_timing, startup);
        ftl.ArrivalTimeOverride = arrival; ftl.TravelTime = Math.Max(arrival, seconds - startup);
    }
    private float FlightMultiplier(EntityUid ship) => _shuttles.TryGetFTLDrive(ship, out _, out _) ? 1 : 4;
    private void OnSectorFtlRequest(Entity<SectorExpeditionShipComponent> entity, ref Content.Server.Shuttles.Events.FTLRequestEvent args) => ConfigurePlanetFlight(entity);
    public void ConfigurePlanetFlight(EntityUid ship)
    {
        if (!Enabled || !_trips.TryGetValue(ship, out var trip) || !TryComp<FTLComponent>(ship, out var ftl)) return;
        var multiplier = FlightMultiplier(ship);
        ftl.StartupTime = Math.Min(2.5f, Math.Max(1, ftl.StartupTime)) * multiplier;
        ftl.TravelTime = 6.5f * multiplier; ftl.ArrivalTimeOverride = 1.5f * multiplier;
        ftl.StateTime = StartEndTime.FromCurTime(_timing, ftl.StartupTime);
        if (_transform.GetMap(ftl.TargetCoordinates) is { } map && (HasComp<SectorExpeditionMapComponent>(map) || trip.LocalPlanet && map == trip.HomeMap) && trip.Phase == Phase.Planet)
            trip.Phase = Phase.LeavingPlanet;
    }
    private async Task Land(ICommonSession session, EntityUid console, EntityUid ship, EconomyAccount account, int planet)
    {
        if (!_trips.TryGetValue(ship, out var trip))
        {
            if (HasComp<SectorExpeditionMapComponent>(Transform(ship).MapUid)) throw new InvalidOperationException("Нет принятого заказа на исследование");
            await LandHome(session, console, ship, account, planet); return;
        }
        if (trip.Phase != Phase.Orbit || trip.User != session.UserId.UserId || trip.Profile != account.Profile)
            throw new InvalidOperationException("Посадка доступна в исследуемом секторе капитану рейса");
        ValidateShip(ship);
        var world = _voyages.WorldSnapshot; var count = SectorResearchDefinition.Count(trip.Sector, world.Seed);
        if (!ResearchLandingAllowed(trip, planet)) throw new InvalidOperationException("Нет принятого заказа для этой планеты");
        if (trip.LocalPlanet)
        {
            var planets = HomePlanetDefinitions(trip.HomeMap);
            if (planet < 0 || planet >= planets.Length || !SolidPlanet(planets[planet]) || Transform(ship).MapUid != trip.HomeMap ||
                Vector2.Distance(_transform.GetWorldPosition(ship), new Vector2(planets[planet].X, planets[planet].Y)) > _config.GetCVar(SectorExpeditionCVars.LandRange))
                throw new InvalidOperationException("Подлетите ближе к доступной для посадки планете");
        }
        if (!trip.LocalPlanet && (!_orbits.TryGetValue(trip.Sector, out var orbit) || !orbit.Markers.TryGetValue(planet, out var marker) ||
            Vector2.Distance(_transform.GetWorldPosition(ship), _transform.GetWorldPosition(marker)) > _config.GetCVar(SectorExpeditionCVars.LandRange)))
            throw new InvalidOperationException("Подлетите ближе к выбранной планете");
        if (!trip.LocalPlanet && (_encounterBusy.Contains(trip.Sector) || _orbits[trip.Sector].Pirates.Any(p => !p.Counted)))
            throw new InvalidOperationException("Посадка недоступна, пока сектор контролируют пиратские корабли");
        if (_salvage.GridExpeditionSettings(ship) is { Enabled: false }) throw new InvalidOperationException("Экспедиции на этом шаттле отключены");
        var key = (PlanetSessionKey(trip), planet);
        if (!_planetLaunching.Add(key)) throw new InvalidOperationException("Подготовка планеты другим экипажем ещё не завершена");
        var leased = false;
        try
        {
            if (_planetMaps.TryGetValue(key, out var map) && _visits.ContainsKey(map))
            {
                if (!CanJoinPlanet(map)) throw new InvalidOperationException("Экспедиция завершается: присоединиться к ней уже нельзя");
                if (!CanUse(session, console)) throw new InvalidOperationException("Доступ к консоли потерян");
                _maps.SetPaused(Comp<MapComponent>(map).MapId, false);
                CaptureCrew(trip);
                var landing = PrepareSectorLanding(ship, map, _landingOrigins[map]);
                trip.Planet = planet; trip.PlanetMap = map; trip.Console = console; trip.Phase = Phase.GeneratingPlanet;
                try
                {
                    Jump(ship, landing, Angle.Zero, 9 * FlightMultiplier(ship));
                    var visit = new Visit(trip.Id, trip.Cycle, trip.Sector, planet, trip.User, trip.Profile, trip.Slot, trip.Service, ship, SessionKey: key.Item1, Local: trip.LocalPlanet);
                    var previous = _visits[map].FindIndex(v => v.Trip == trip.Id);
                    if (previous >= 0) _visits[map][previous] = visit;
                    else _visits[map].Add(visit);
                }
                catch { _landingReservations[map].Remove(ship); trip.PlanetMap = null; throw; }
                _planetLaunching.Remove(key);
                return;
            }
            if (!trip.LocalPlanet) { await _db.LeaseSectorPlanetAsync(trip.Cycle, trip.Sector, planet, count, trip.Id, _run, false); leased = true; }
            if (!CanUse(session, console) || !_voyages.Ready) throw new InvalidOperationException("Доступ к консоли потерян");
            ValidateShip(ship); CaptureCrew(trip); trip.Planet = planet; trip.Phase = Phase.GeneratingPlanet; trip.Console = console;
            _salvage.BeginSectorPlanet(ship, console, SectorResearchDefinition.MissionParameters(trip.Sector, planet, world.Seed));
        }
        catch
        {
            _planetLaunching.Remove(key); trip.Phase = Phase.Orbit;
            if (leased) await _db.LeaseSectorPlanetAsync(trip.Cycle, trip.Sector, planet, count, trip.Id, _run, true);
            throw;
        }
    }
    private void OnPlanetGenerated(EntityUid entity, SectorExpeditionShipComponent component, SectorPlanetGeneratedEvent args)
    {
        if (!_trips.TryGetValue(entity, out var trip)) return;
        trip.PlanetMap = args.Map; _planetMaps[(PlanetSessionKey(trip), trip.Planet)] = args.Map;
        _visits[args.Map] = [new(trip.Id, trip.Cycle, trip.Sector, trip.Planet, trip.User, trip.Profile, trip.Slot, trip.Service, trip.Ship, SessionKey: PlanetSessionKey(trip), Local: trip.LocalPlanet)];
    }
    public void SectorGenerationResult(EntityUid entity, bool success)
    {
        if (!_trips.TryGetValue(entity, out var trip)) return;
        _planetLaunching.Remove((PlanetSessionKey(trip), trip.Planet));
        if (!success) _ = ReleaseFailedGeneration(trip);
    }
    private async Task ReleaseFailedGeneration(Trip trip)
    {
        try
        {
            if (!trip.LocalPlanet) await _db.LeaseSectorPlanetAsync(trip.Cycle, trip.Sector, trip.Planet, SectorResearchDefinition.Count(trip.Sector, _voyages.WorldSnapshot.Seed), trip.Id, _run, true);
            if (trip.PlanetMap is { } map)
            {
                _visits.Remove(map); _landingOrigins.Remove(map); _landingReservations.Remove(map);
                _planetMaps.Remove((PlanetSessionKey(trip), trip.Planet));
            }
            trip.Phase = Phase.Orbit; trip.PlanetMap = null;
        }
        catch (Exception e) { Log.Warning($"Sector generation lease release deferred: {e.Message}"); }
    }
    private void LeavePlanet(EntityUid ship)
    {
        if (!_trips.TryGetValue(ship, out var trip) || trip.Phase != Phase.Planet)
            throw new InvalidOperationException("Шаттл не находится на планете");
        ValidateShip(ship); trip.Phase = Phase.LeavingPlanet;
        // Keep the native departure pipeline: it records return locations and invokes body-insurance rescue hooks.
        var target = trip.LocalPlanet ? trip.Home : new EntityCoordinates(_orbits[trip.Sector].Map, PlanetPosition(trip.Sector, trip.Planet) + new Vector2(80, 0));
        Jump(ship, target, trip.LocalPlanet ? trip.HomeAngle : Angle.Zero, 9 * FlightMultiplier(ship));
    }
    private void ReturnHome(EntityUid ship)
    {
        if (!_trips.TryGetValue(ship, out var trip)) throw new InvalidOperationException("Шаттл уже в родном секторе");
        if (trip.Phase == Phase.Planet) { trip.HomeAfterPlanet = true; LeavePlanet(ship); return; }
        if (trip.Phase != Phase.Orbit) throw new InvalidOperationException("Дождитесь завершения текущего перелёта");
        ValidateShip(ship);
        var homePath = _voyages.RouteGraph.Path(VoyageRouteModel.Home, trip.Sector); trip.Path = homePath.Length > 1 ? homePath : trip.HomePath;
        var seconds = JourneySeconds(ship, trip.Path); if (seconds <= 0) throw new InvalidOperationException("БСС-двигатель недоступен");
        trip.Depart = Now; trip.Arrive = Now + (long)(seconds * 1000); trip.Phase = trip.Wiped ? Phase.EmergencyHome : Phase.ReturningHome;
        Jump(ship, trip.Home, trip.HomeAngle, seconds);
    }
    private void OnFtlCompleted(ref FTLCompletedEvent args)
    {
        if (_trips.TryGetValue(args.Entity, out var trip)) _ = FinishArrival(trip, args.MapUid);
    }
    private async Task FinishArrival(Trip trip, EntityUid map)
    {
        try
        {
            if (map == trip.HomeMap)
            {
                ReleaseEmergencyCrew(trip);
                _researchClaims.Remove((trip.User, trip.Profile));
                _trips.Remove(trip.Ship); RemComp<SectorExpeditionShipComponent>(trip.Ship);
                _hireSettlements[trip.Id] = trip; return;
            }
            if (HasComp<SalvageExpeditionComponent>(map)) { trip.Phase = Phase.Planet; return; }
            if (TryComp<SectorExpeditionMapComponent>(map, out var orbit))
            {
                trip.Phase = Phase.Orbit; trip.Sector = orbit.Sector;
                RegisterPirateArrival(trip, map);
                await _db.DiscoverSectorAsync(trip.Cycle, trip.Sector); await _voyages.RefreshGalaxy(); _nextAutoOrders = TimeSpan.Zero;
                if (trip.Wiped || trip.HomeAfterPlanet) _emergencyPending.Add(trip.Ship);
            }
        }
        catch (Exception e) { Log.Warning($"Sector arrival persistence deferred: {e.Message}"); }
    }
    private void OnSectorFtlStarted(ref FTLStartedEvent args)
    {
        if (args.FromMapUid is not { } map || !_visits.TryGetValue(map, out var visits) || !_trips.TryGetValue(args.Entity, out var trip)) return;
        var index = visits.FindIndex(v => v.Trip == trip.Id);
        if (index < 0) return;
        var complete = TryComp<SalvageExpeditionComponent>(map, out var expedition) && expedition.Completed;
        if (!complete) visits[index] = visits[index] with { Failed = true };
        else if (!visits[index].Failed && _workProof.Add((map, trip.Id))) trip.CompletedWork++;
        if (_landingReservations.TryGetValue(map, out var reservations)) reservations.Remove(args.Entity);
    }
    public bool SectorRewardAllowed(EntityUid map) => !_visits.TryGetValue(map, out var visits) || visits.Any(v => !v.Failed);
    public void SectorPlanetFinished(EntityUid map, bool complete)
    {
        if (!_visits.Remove(map, out var visits)) return;
        if (_retainedPlanets.Contains(map)) _visits[map] = new();
        else
        {
            _planetMaps.Remove((visits[0].SessionKey, visits[0].Planet));
            _landingOrigins.Remove(map); _landingReservations.Remove(map);
        }
        _completions[map] = (visits.ToArray(), complete && visits.Any(v => !v.Failed));
        foreach (var visit in visits)
            if (complete && !visit.Failed && _workProof.Add((map, visit.Trip)) && _trips.TryGetValue(visit.Ship, out var trip) && trip.Id == visit.Trip) trip.CompletedWork++;
    }
    private async Task SettlePlanet(EntityUid map, Visit[] visits, bool complete)
    {
        var leader = visits[0];
        try
        {
            if (complete && !leader.Local)
            {
                var teams = visits.Where(v => !v.Failed).Select(v => new SectorResearchParticipant(v.User, v.Profile, v.Service)).ToArray();
                var payments = await _db.CompleteSectorPlanetTeamsAsync(leader.Cycle, leader.Sector, leader.Planet, leader.Trip, _run, teams, 15 + leader.Planet);
                foreach (var payment in payments)
                {
                    var visit = visits.First(v => v.User == payment.User && v.Profile == payment.Profile);
                    await _bank.PublishExchangeBalance(new(visit.User), visit.Slot);
                    Log.Info($"Sector research {visit.Sector}/{visit.Planet}, trip={visit.Trip}, profile={visit.Profile}, teams={teams.Length}, reward={payment.Amount}");
                }
            }
            else if (!leader.Local) await _db.LeaseSectorPlanetAsync(leader.Cycle, leader.Sector, leader.Planet, SectorResearchDefinition.Count(leader.Sector, _voyages.WorldSnapshot.Seed), leader.Trip, _run, true);
            _completions.Remove(map);
            if (!_retainedPlanets.Contains(map)) _workProof.RemoveWhere(p => p.Map == map);
            await _voyages.RefreshGalaxy(); _nextAutoOrders = TimeSpan.Zero;
        }
        catch (InvalidOperationException e) { _completions.Remove(map); Log.Warning($"Sector completion rejected: {e.Message}"); }
        catch (Exception e) { Log.Warning($"Sector completion deferred: {e.Message}"); }
        finally { _settling.Remove(map); }
    }
    private readonly HashSet<EntityUid> _emergencyPending = new();
    private void Emergency(Trip trip)
    {
        trip.Wiped = true;
        MarkVisitFailed(trip);
        _emergencyPending.Add(trip.Ship);
        SecureEmergencyCrew(trip);
        // Bodies remain dead; this is emergency retrieval, never resurrection or a second insurance payout.
        Log.Info($"Sector trip {trip.Id}: all crew lost, emergency return queued.");
    }
}
