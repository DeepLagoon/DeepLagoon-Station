using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Economy;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._NF.Bank;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Salvage;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._DeepLagoon.Voyages;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Access.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
namespace Content.Server._DeepLagoon.Voyages;

/// <summary>Human sector travel. Orbit metadata never invokes the procedural space generator.</summary>
public sealed partial class SectorExpeditionSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private VoyageSystem _voyages = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private SalvageSystem _salvage = default!;
    [Dependency] private MapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private Content.Shared.Power.EntitySystems.SharedPowerReceiverSystem _power = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Guid _run = Guid.NewGuid();
    private readonly Dictionary<EntityUid, Trip> _trips = new();
    private readonly Dictionary<string, Orbit> _orbits = new();
    private readonly HashSet<Guid> _busy = new();
    private readonly Dictionary<Guid, TimeSpan> _requests = new();
    private TimeSpan _next;
    public bool Enabled => _config.GetCVar(SectorExpeditionCVars.Enabled);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private enum Phase { Orbit, Departing, GeneratingPlanet, Planet, LeavingPlanet, ReturningHome, EmergencyHome }
    private sealed class Trip
    {
        public required Guid Id, User, Cycle;
        public required int Profile, Slot;
        public required EntityUid Ship, Console, HomeMap;
        public required EntityCoordinates Home;
        public required Angle HomeAngle;
        public required string Sector, Name;
        public string[] Path = [], HomePath = [];
        public bool HomeAfterPlanet;
        public Phase Phase;
        public int Planet = -1, CompletedWork;
        public EntityUid? PlanetMap;
        public Guid Service, Hire;
        public readonly HashSet<EntityUid> Bodies = new();
        public long Depart, Arrive;
        public EntityUid? PirateAlertMap;
        public TimeSpan PirateAttackAt;
        public bool Busy, Wiped, LocalPlanet;
    }
    private sealed class Orbit
    {
        public required EntityUid Map;
        public required string Sector;
        public readonly Dictionary<int, EntityUid> Markers = new();
        public readonly List<PirateShip> Pirates = new();
#if DEBUG
        public bool TestEncounter;
#endif
    }
    public override void Initialize()
    {
#if DEBUG
        InitializePirateTestStand();
#endif
        Subs.CVar(_config, SectorResearchSettings.Budget, _ => { _researchSettingsVersion++; _nextAutoOrders = TimeSpan.Zero; });
        Subs.CVar(_config, SectorResearchSettings.RefreshSeconds, _ => { _researchSettingsVersion++; _nextAutoOrders = TimeSpan.Zero; });
        SubscribeNetworkEvent<SectorExpeditionRequest>(OnRequest);
        SubscribeLocalEvent<Content.Shared.Weapons.Ranged.Components.GunComponent, Content.Shared.Weapons.Ranged.Systems.GunShotEvent>(OnSectorGunShot);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFtlCompleted);
        SubscribeLocalEvent<FTLStartedEvent>(OnSectorFtlStarted);
        SubscribeLocalEvent<SectorExpeditionShipComponent, Content.Server.Shuttles.Events.FTLRequestEvent>(OnSectorFtlRequest);
        SubscribeLocalEvent<SectorExpeditionShipComponent, SectorPlanetGeneratedEvent>(OnPlanetGenerated);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => {
#if DEBUG
            _testEncounters.Clear(); _testPilots.Clear(); _testShips.Clear();
#endif
            _trips.Clear(); _orbits.Clear(); _requests.Clear(); _busy.Clear(); _visits.Clear(); _completions.Clear(); _emergencyPending.Clear(); _hireSettlements.Clear(); _workProof.Clear(); _planetMaps.Clear(); _planetLaunching.Clear(); _landingOrigins.Clear(); _landingReservations.Clear(); _retainedPlanets.Clear(); _researchClaims.Clear(); _pendingAutoOrders.Clear(); _nextAutoOrders = TimeSpan.Zero;
        });
    }
    private bool CanUse(ICommonSession session, EntityUid console) => Enabled && Exists(console) &&
        TryComp<SalvageExpeditionConsoleComponent>(console, out var terminal) && !terminal.DispatchByDisk &&
        session.AttachedEntity is { } actor && !HasComp<IronmanComponent>(actor) &&
        TryComp<MobStateComponent>(actor, out var mob) && mob.CurrentState == MobState.Alive &&
        _ui.IsUiOpen(console, SalvageConsoleUiKey.Expedition, actor) && _interaction.InRangeUnobstructed(actor, console);
    private bool CanPilot(ICommonSession session, EntityUid console) => session.AttachedEntity is { } actor && _access.IsAllowed(actor, console);
    private EntityUid GetShip(EntityUid console)
    {
        if (Transform(console).GridUid is not { } ship || !HasComp<ShuttleComponent>(ship)) throw new InvalidOperationException("Консоль должна находиться на шаттле");
        return ship;
    }
    private Guid VesselIdentity(EntityUid ship)
    {
        if (TryComp<ParkingShuttleComponent>(ship, out var parking) && parking.Identity != Guid.Empty) return parking.Identity;
        var identity = EnsureComp<SectorCrewIdentityComponent>(ship); if (identity.Identity == Guid.Empty) identity.Identity = Guid.NewGuid(); return identity.Identity;
    }
    private Guid ServiceIdentity(EntityUid ship, Guid user, int profile)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(VesselIdentity(ship).ToString("N") + user.ToString("N") + profile));
        return new Guid(bytes.AsSpan(0, 16));
    }
    private bool OwnsShip(ICommonSession session, EntityUid ship) => TryComp<ShipOwnershipComponent>(ship, out var owner) && owner.OwnerUserId == session.UserId ||
        TryComp<ParkingShuttleComponent>(ship, out var parking) && parking.OwnerAccount == session.UserId.UserId;
    private async void OnRequest(SectorExpeditionRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryGetEntity(request.Console, out var entity) || entity is not { } console || !CanUse(session, console)) return;
        var user = session.UserId.UserId;
        if (!_voyages.Ready) { Status(session, request.Console, "Карта секторов ещё загружается"); return; }
        if (request.Action != SectorExpeditionAction.Refresh && _requests.TryGetValue(user, out var next) && _timing.RealTime < next || !_busy.Add(user)) return;
        if (request.Action != SectorExpeditionAction.Refresh) _requests[user] = _timing.RealTime + TimeSpan.FromMilliseconds(250);
        var payment = false;
        try
        {
            if (!_bank.TryGetCharacter(session, out _, out _, out var slot)) return;
            var account = await _db.ReadEconomyAccountAsync(user, slot);
            if (!CanUse(session, console) || !_bank.IsCharacter(session, slot)) return;
            var ship = GetShip(console);
            if (request.Action == SectorExpeditionAction.Refresh) { await Board(session, console, account, request.Sector, ""); return; }
            if (!CanPilot(session, console)) throw new InvalidOperationException("Нет доступа к управлению экспедицией");
            if (request.Action is SectorExpeditionAction.PostResearch or SectorExpeditionAction.CancelResearch or SectorExpeditionAction.HireCrew or SectorExpeditionAction.ConfirmHire or SectorExpeditionAction.CancelHire or SectorExpeditionAction.DeclineHire)
            {
                if (!_bank.BeginExchangePayment(session.UserId)) throw new InvalidOperationException("Другая банковская операция ещё не завершена");
                payment = true;
            }
            if (request.Action == SectorExpeditionAction.AcceptResearch) await AcceptResearch(session, console, ship, account, request);
            else if (request.Action == SectorExpeditionAction.AbandonResearch) AbandonResearch(session, account);
            else if (request.Action == SectorExpeditionAction.Depart) await Depart(session, console, ship, account, request);
            else if (request.Action == SectorExpeditionAction.Land) await Land(session, console, ship, account, request.Planet);
            else if (request.Action == SectorExpeditionAction.LeavePlanet) LeavePlanet(ship);
            else if (request.Action == SectorExpeditionAction.ReturnHome) ReturnHome(ship);
            else await MarketAction(session, console, ship, account, request);
            await _bank.PublishExchangeBalance(session.UserId, slot);
            await Board(session, console, await _db.ReadEconomyAccountAsync(user, slot), request.Sector, "");
        }
        catch (Exception e) { Log.Warning($"Sector expedition request rejected/deferred: {e.Message}"); Status(session, request.Console, e is InvalidOperationException ? e.Message : "Операция не завершена. Обновите состояние"); }
        finally { if (payment) _bank.EndExchangePayment(session.UserId); _busy.Remove(user); }
    }
    private void Status(ICommonSession session, NetEntity console, string text) => RaiseNetworkEvent(new SectorExpeditionSnapshot { Console = console, Status = text }, session);
    public override void Update(float frameTime)
    {
        if (!Enabled || !_voyages.Ready || _timing.RealTime < _next) return;
        _next = _timing.RealTime + TimeSpan.FromSeconds(1);
        foreach (var trip in _trips.Values.ToArray())
        {
            if (!Exists(trip.Ship)) { RescueDestroyedShip(trip); continue; }
            if (trip.Busy) continue;
            var bodies = trip.Bodies.Where(Exists).ToArray();
            if (!trip.Wiped && trip.Bodies.Count > 0 && bodies.All(b => TryComp<MobStateComponent>(b, out var state) && state.CurrentState == MobState.Dead))
                Emergency(trip);
        }
        foreach (var pair in _completions.ToArray()) if (_settling.Add(pair.Key)) _ = SettlePlanet(pair.Key, pair.Value.Visits, pair.Value.Complete);
        foreach (var ship in _emergencyPending.ToArray())
        {
            if (!_trips.TryGetValue(ship, out var trip)) { _emergencyPending.Remove(ship); continue; }
            if (HasComp<FTLComponent>(ship)) continue;
            try { if (trip.Wiped) EmergencyReturn(trip); else ReturnHome(ship); if (trip.Phase is Phase.EmergencyHome or Phase.ReturningHome) _emergencyPending.Remove(ship); }
            catch (InvalidOperationException) { }
        }
        foreach (var trip in _hireSettlements.Values.ToArray()) if (_settlingHires.Add(trip.Id)) _ = SettleHumanHire(trip);
        UpdatePirates(); UnloadEmptyOrbits();
        if (!_autoOrdersBusy && _timing.RealTime >= _nextAutoOrders) _ = EnsureAutomaticOrders();
    }
}
