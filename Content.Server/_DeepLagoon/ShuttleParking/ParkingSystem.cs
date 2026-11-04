using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Currency;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Station.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Access.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.GameTicking;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Ghost;
using Content.Shared.Interaction;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.ShuttleParking;

/// <summary>Independent implementation; LuaCorp parking was consulted only as a behaviour reference.</summary>
public sealed partial class ParkingSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MapSystem _map = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private StationSystem _stations = default!;
    [Dependency] private ShipyardSystem _shipyard = default!;
    [Dependency] private ShipOwnershipSystem _ownership = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private LagoonCoinSystem _coins = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private FollowerSystem _follower = default!;
    private ParkingStore _store = default!;
    private readonly HashSet<Guid> _busy = [];
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _viewers = [];
    private readonly Dictionary<EntityUid, EntityUid> _parkingMaps = [];
    private readonly Dictionary<Guid, Review> _reviews = [];
    private readonly Dictionary<Guid, EntityUid> _activeShips = [];
    private readonly Dictionary<EntityUid, TimeSpan> _refreshAfter = [];
    private EntityUid? _staging;
    private int _epoch;
    private sealed record Context(EntityUid Console, EntityUid Actor, EntityUid Card, ICommonSession Session, int Character);
    private sealed record Review(Guid Token, Guid Slot, EntityUid Grid, string Hash, TimeSpan Expires);

    public override void Initialize()
    {
        base.Initialize();
        _store = new ParkingStore(_resources.UserData.RootDir is { } root ? Path.Combine(root, "shuttle-parking.db") : ":memory:");
        SubscribeLocalEvent<ParkingConsoleComponent, ComponentInit>((uid, comp, _) => _slots.AddItemSlot(uid, ParkingConsoleComponent.IdSlot, comp.Card));
        SubscribeLocalEvent<ParkingConsoleComponent, ComponentRemove>((uid, comp, _) => _slots.RemoveItemSlot(uid, comp.Card));
        SubscribeLocalEvent<ParkingConsoleComponent, BoundUIOpenedEvent>(OnOpen);
        SubscribeLocalEvent<ParkingConsoleComponent, BoundUIClosedEvent>(OnClose);
        SubscribeLocalEvent<ParkingConsoleComponent, ParkingAction>(OnAction);
        SubscribeLocalEvent<ParkingConsoleComponent, EntInsertedIntoContainerMessage>((uid, _, _) => RefreshViewers(uid));
        SubscribeLocalEvent<ParkingConsoleComponent, EntRemovedFromContainerMessage>((uid, _, _) => RefreshViewers(uid));
        SubscribeLocalEvent<ShipyardShuttlePurchaseEvent>(OnPurchased);
        SubscribeLocalEvent<ParkingShuttleComponent, EntityTerminatingEvent>((Entity<ParkingShuttleComponent> entity, ref EntityTerminatingEvent ev) =>
        {
            if (_activeShips.TryGetValue(entity.Comp.Identity, out var active) && active == entity.Owner) _activeShips.Remove(entity.Comp.Identity);
        });
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => { _epoch++; _parkingMaps.Clear(); _viewers.Clear(); _reviews.Clear(); _activeShips.Clear(); _refreshAfter.Clear(); _staging = null; });
    }
    public override void Shutdown() { _store.Dispose(); base.Shutdown(); }
    private IEnumerable<EntityUid> Descendants(EntityUid root)
    {
        var pending = new Stack<EntityUid>(); pending.Push(root);
        while (pending.TryPop(out var entity))
        {
            yield return entity;
            var children = Transform(entity).ChildEnumerator;
            while (children.MoveNext(out var child)) pending.Push(child);
        }
    }
    private void OnPurchased(ShipyardShuttlePurchaseEvent ev)
    {
        if (!TryComp<ActorComponent>(ev.Purchaser, out var actor) || !TryComp<VesselComponent>(ev.Shuttle, out var vessel)
            || !_prototypes.TryIndex(vessel.VesselId, out var prototype)) return;
        RegisterPurchasedShip(ev.Shuttle, actor.PlayerSession.UserId.UserId, prototype.Price, prototype.ID, Name(ev.Purchaser));
    }
    public void RegisterPurchasedShip(EntityUid grid, Guid owner, int price, string vessel, string ownerName)
    {
        var data = EnsureComp<ParkingShuttleComponent>(grid);
        data.Identity = Guid.NewGuid(); data.OwnerAccount = owner; data.OriginalPrice = price; data.Vessel = vessel; data.OwnerName = ownerName;
        data.OriginalItems.Clear();
        foreach (var entity in Descendants(grid))
        {
            var original = EnsureComp<ParkingOriginalComponent>(entity);
            original.Shuttle = data.Identity; original.Identity = Guid.NewGuid();
            data.OriginalItems[original.Identity] = entity;
        }
        _activeShips[data.Identity] = grid;
    }
    private Context? GetContext(EntityUid console, EntityUid actor)
    {
        if (!Exists(console) || !Exists(actor) || !TryComp<ActorComponent>(actor, out var session)
            || !TryComp<ParkingConsoleComponent>(console, out var comp) || comp.Card.Item is not { } card
            || TryComp<Content.Server.Power.Components.ApcPowerReceiverComponent>(console, out var power) && !power.Powered
            || !TryComp<IdCardComponent>(card, out var id) || id.FullName?.Trim() != Name(actor).Trim()
            || !_interaction.InRangeUnobstructed(actor, console) || !_preferences.TryGetCachedPreferences(session.PlayerSession.UserId, out var prefs)
            || prefs.SelectedCharacter is not HumanoidCharacterProfile) return null;
        return new(console, actor, card, session.PlayerSession, prefs.SelectedCharacterIndex);
    }
    private EntityUid? OwnedShip(Context context)
    {
        if (!TryComp<ShuttleDeedComponent>(context.Card, out var deed) || deed.ShuttleUid is not { } grid || !Exists(grid)
            || !HasComp<MapGridComponent>(grid) || !HasComp<ShuttleComponent>(grid)) return null;
        if (!TryComp<ParkingShuttleComponent>(grid, out var ship))
        {
            // Legacy ships have no trustworthy original-item provenance. Do not bless their current cargo.
            if (!TryComp<ShipOwnershipComponent>(grid, out var ownership) || ownership.OwnerUserId != context.Session.UserId
                || !TryComp<VesselComponent>(grid, out var vessel) || !_prototypes.TryIndex(vessel.VesselId, out var prototype)) return null;
            ship = EnsureComp<ParkingShuttleComponent>(grid);
            ship.Identity = Guid.NewGuid(); ship.OwnerAccount = context.Session.UserId.UserId; ship.OriginalPrice = prototype.Price;
            ship.Vessel = prototype.ID; ship.OwnerName = Name(context.Actor);
            _activeShips[ship.Identity] = grid;
        }
        return ship.OwnerAccount == context.Session.UserId.UserId && _activeShips.TryGetValue(ship.Identity, out var active) && active == grid ? grid : null;
    }
    private void OnOpen(EntityUid uid, ParkingConsoleComponent comp, BoundUIOpenedEvent ev)
    {
        if (!_viewers.TryGetValue(uid, out var viewers)) _viewers[uid] = viewers = [];
        viewers.Add(ev.Actor); _ = Publish(uid, ev.Actor);
    }
    private void OnClose(EntityUid uid, ParkingConsoleComponent comp, BoundUIClosedEvent ev)
    {
        if (_viewers.TryGetValue(uid, out var viewers)) viewers.Remove(ev.Actor);
    }
    private async void RefreshViewers(EntityUid console)
    {
        if (!_viewers.TryGetValue(console, out var viewers)) return;
        foreach (var actor in viewers.ToArray()) await Publish(console, actor);
    }
    private async Task Publish(EntityUid console, EntityUid actor, string message = "")
    {
        try { await PublishState(console, actor, message); }
        catch (Exception e) { Log.Error($"Parking interface failed for {actor}: {e}"); }
    }
    private async Task PublishState(EntityUid console, EntityUid actor, string message)
    {
        if (!Exists(actor) || !Exists(console) || !_viewers.TryGetValue(console, out var viewers) || !viewers.Contains(actor)) return;
        var context = GetContext(console, actor);
        var state = new ParkingUpdate { Message = message };
        if (context != null)
        {
            var user = context.Session.UserId.UserId;
            state.IdPresent = true; state.User = Name(actor) + " · " + context.Session.Name; state.Busy = _busy.Contains(user);
            state.BuyToken = Guid.NewGuid();
            if (TryComp<BankAccountComponent>(actor, out var bank)) state.Credits = bank.Balance;
            if (OwnedShip(context) is { } grid)
            {
                state.HasShip = true; state.ShipName = Name(grid); state.OwnerName = Comp<ParkingShuttleComponent>(grid).OwnerName;
                state.Category = GetCategory(grid); state.Parked = Comp<ParkingShuttleComponent>(grid).Parked;
            }
            foreach (var slot in _store.Slots(user))
                state.Places.Add(new(slot.Id, slot.Category, slot.Ship?.Name ?? "", slot.Ship?.OwnerName ?? "", slot.Ship?.Category ?? -1,
                    slot.Ship == null ? 0 : ParkingRules.RedemptionPrice(slot.Ship.OriginalPrice), slot.Phase == "redeeming", slot.Paid));
            state.Docks = AvailableDocks(console);
            if (_reviews.TryGetValue(user, out var review) && review.Expires > _timing.RealTime && Exists(review.Grid))
            {
                state.ReviewToken = review.Token; state.ReviewSlot = review.Slot;
                state.Excluded = ExcludedEntities(review.Grid).Select(entity => Name(entity)).Take(500).ToList();
            }
            try { state.Coins = await _database.GetLagoonCoinsAsync(context.Session.UserId); }
            catch (Exception e) { Log.Warning($"Parking balance unavailable: {e.GetType().Name}"); }
            if (GetContext(console, actor)?.Card != context.Card) return;
        }
        else if (message.Length == 0) state.Message = "Вставьте свою ID-карту для управления парковкой.";
        if (Exists(console) && Exists(actor) && _viewers.TryGetValue(console, out viewers) && viewers.Contains(actor))
            _ui.ServerSendUiMessage(console, ParkingUiKey.Key, state, actor);
    }
    private async void OnAction(EntityUid uid, ParkingConsoleComponent comp, ParkingAction ev)
    {
        if (ev.Action == "refresh")
        {
            if (_refreshAfter.TryGetValue(ev.Actor, out var after) && after > _timing.RealTime) return;
            _refreshAfter[ev.Actor] = _timing.RealTime + TimeSpan.FromSeconds(1);
            await Publish(uid, ev.Actor); return;
        }
        var context = GetContext(uid, ev.Actor);
        if (context == null) { await Publish(uid, ev.Actor, "Нужна ваша ID-карта и доступ к консоли."); return; }
        var user = context.Session.UserId.UserId;
        if (!_busy.Add(user)) return;
        var message = "";
        try
        {
            switch (ev.Action)
            {
                case "park": Park(uid, OwnedShip(context) ?? throw new InvalidOperationException("На карте нет вашего шаттла.")); message = "Шаттл заморожен на бесплатной парковке."; break;
                case "recall":
                    var grid = OwnedShip(context) ?? throw new InvalidOperationException("На карте нет вашего шаттла.");
                    if (!Comp<ParkingShuttleComponent>(grid).Parked) throw new InvalidOperationException("Шаттл не находится на парковке.");
                    Deliver(context, grid, ev.Dock); message = "Шаттл возвращён бесплатно."; break;
                case "preview": PrepareReview(context, ev.Slot); message = "Проверьте список исключаемых предметов перед сохранением."; break;
                case "save": Save(context, ev.Slot, ev.Token); message = "Шаттл сохранён. Исключённые предметы оставлены у консоли."; break;
                case "cancel": _reviews.Remove(user); break;
                case "buy":
                    var purchase = _store.BeginPurchase(user, ev.Token, ev.Category);
                    if (!purchase.Completed)
                    {
                        try { await _coins.Spend(context.Session.UserId, purchase.Cost, $"parking:{user:N}:{purchase.Id:N}", "Место хранения шаттла"); }
                        catch (InsufficientLagoonCoinsException) { _store.CancelPurchase(user, purchase.Id); throw; }
                        _store.GrantPurchase(user, purchase);
                    }
                    message = "Место долгосрочного хранения приобретено."; break;
                case "redeem": await Redeem(context, ev.Slot, ev.Dock); message = "Шаттл выдан. Управление привязано к вашей ID-карте."; break;
            }
        }
        catch (InsufficientLagoonCoinsException) { message = "Недостаточно Lagoon Coin."; }
        catch (Exception e)
        {
            Log.Error($"Parking operation {ev.Action} for {user} failed: {e}");
            message = e is InvalidOperationException or ArgumentException ? e.Message : "Операция не завершена. Попробуйте ещё раз; учтённая оплата повторно не списывается.";
        }
        finally { _busy.Remove(user); }
        await Publish(uid, ev.Actor, message);
    }
    public int GetCategory(EntityUid grid)
    {
        var mapGrid = Comp<MapGridComponent>(grid);
        return ParkingRules.Category(_map.GetAllTiles(grid, mapGrid).Count(), mapGrid.LocalAABB.Width, mapGrid.LocalAABB.Height);
    }
    private void CheckEmpty(EntityUid grid)
    {
        var ghosts = new List<EntityUid>();
        foreach (var entity in Descendants(grid))
        {
            if (HasComp<GhostComponent>(entity))
            {
                ghosts.Add(entity);
                continue;
            }
            if (HasComp<ActorComponent>(entity)
                || (TryComp<MindContainerComponent>(entity, out var mind) && mind.Mind != null))
                throw new InvalidOperationException("На шаттле остался пассажир или живое существо: " + Name(entity));
        }
        if (HasComp<Content.Shared.Shuttles.Components.FTLComponent>(grid)) throw new InvalidOperationException("Дождитесь окончания FTL-перехода.");
        ReleaseGhosts(ghosts);
    }

    private void ReleaseGhosts(List<EntityUid> ghosts)
    {
        foreach (var entity in ghosts)
        {
            if (!Exists(entity) || !TryComp<TransformComponent>(entity, out var ghostTransform)) continue;
            if (ghostTransform.MapUid is not { } map) continue;
            var world = _transform.GetWorldPosition(entity);
            _containers.TryRemoveFromContainer(entity);
            if (TryComp<FollowerComponent>(entity, out var follower) && Exists(follower.Following))
                _follower.StopFollowingEntity(entity, follower.Following);
            _transform.SetCoordinates(entity, new EntityCoordinates(map, world));
        }
    }
    private void CheckDocked(EntityUid console, EntityUid grid)
    {
        if (Transform(console).GridUid == grid) throw new InvalidOperationException("Используйте консоль за пределами сохраняемого шаттла.");
        var station = _stations.GetOwningStation(console) ?? throw new InvalidOperationException("Консоль не принадлежит станции.");
        if (!_docking.GetDocks(grid).Any(d => d.Comp.DockedWith is { } other && _stations.GetOwningStation(other) == station))
            throw new InvalidOperationException("Сначала пристыкуйте шаттл к станции с этой консолью.");
    }
    public void Park(EntityUid console, EntityUid grid)
    {
        var ship = Comp<ParkingShuttleComponent>(grid);
        if (ship.Parked) throw new InvalidOperationException("Шаттл уже припаркован.");
        CheckEmpty(grid); CheckDocked(console, grid);
        var map = _map.CreateMap(out var id); _map.SetPaused(id, true);
        EnsureComp<ApartmentIsolationComponent>(map);
        var previousCoordinates = Transform(grid).Coordinates;
        try
        {
            _docking.UndockDocks(grid);
            if (_stations.GetOwningStation(grid) is { } formerStation)
            {
                ship.ReturnStation = formerStation;
                _stations.RemoveGridFromStation(formerStation, grid);
            }
            if (TryComp<PhysicsComponent>(grid, out var body))
            {
                _physics.SetLinearVelocity(grid, Vector2.Zero, body: body); _physics.SetAngularVelocity(grid, 0, body: body);
            }
            _transform.SetCoordinates(grid, new EntityCoordinates(map, Vector2.Zero));
            ship.Parked = true; _parkingMaps[grid] = map;
        }
        catch
        {
            if (Exists(grid) && Transform(grid).MapUid == map) _transform.SetCoordinates(grid, previousCoordinates);
            if (Exists(map)) Del(map);
            throw;
        }
    }
    private List<ParkingDock> AvailableDocks(EntityUid console)
    {
        var result = new List<ParkingDock>(); var station = _stations.GetOwningStation(console);
        if (station == null) return result;
        var query = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dock, out _))
            if (dock.DockedWith == null && _stations.GetOwningStation(uid) == station)
                result.Add(new(GetNetEntity(uid), Name(uid) + " · " + GetNetEntity(uid)));
        return result;
    }
    private DockingConfig? SelectedDockConfig(EntityUid grid, EntityUid target, EntityUid dock)
    {
        if (!TryComp<DockingComponent>(dock, out var targetDock) || targetDock.DockedWith != null) return null;
        foreach (var candidate in _docking.GetDocks(grid))
            if (candidate.Comp.DockedWith == null && _docking.GetDockingConfig(grid, target, candidate.Owner, candidate.Comp, dock, targetDock) is { } config)
                return config;
        return null;
    }
    private void Deliver(Context context, EntityUid grid, NetEntity? selected)
    {
        if (selected == null || !AvailableDocks(context.Console).Any(d => d.Entity == selected)) throw new InvalidOperationException("Выберите свободный шлюз этой станции.");
        var dock = GetEntity(selected.Value);
        var target = Transform(dock).GridUid ?? throw new InvalidOperationException("Шлюз недоступен.");
        var config = SelectedDockConfig(grid, target, dock) ?? throw new InvalidOperationException("Шаттл не помещается у выбранного шлюза.");
        _shuttles.FTLDock((grid, Transform(grid)), config);
        var ship = Comp<ParkingShuttleComponent>(grid);
        ship.Parked = false;
        if (ship.ReturnStation is { } former && Exists(former) && HasComp<StationDataComponent>(former))
            _stations.AddGridToStation(former, grid);
        else
            _shipyard.RegisterParkingStation(grid);
        ship.ReturnStation = null;
        _ownership.RegisterShipOwnership(grid, context.Session);
        if (_parkingMaps.Remove(grid, out var parking) && Exists(parking)) Del(parking);
    }
    private async Task Redeem(Context context, Guid slotId, NetEntity? dock)
    {
        if (OwnedShip(context) != null) throw new InvalidOperationException("Сначала сохраните действующий шаттл с ID-карты.");
        var user = context.Session.UserId.UserId;
        var slot = _store.BeginRedeem(user, slotId, context.Character);
        var snapshot = slot.Ship!;
        var grid = LoadSnapshot(snapshot.Grid);
        var epoch = _epoch;
        try
        {
            var restored = Comp<ParkingShuttleComponent>(grid);
            if (restored.Identity != snapshot.Identity || restored.OwnerAccount != user || restored.OriginalPrice != snapshot.OriginalPrice)
                throw new InvalidDataException("Метаданные снимка не совпадают с сохранением.");
            CheckEmpty(grid);
            // Check berth before charging; check it again after the asynchronous bank transaction.
            if (dock == null || !AvailableDocks(context.Console).Any(d => d.Entity == dock)) throw new InvalidOperationException("Выберите свободный шлюз.");
            var dockUid = GetEntity(dock.Value);
            var target = Transform(dockUid).GridUid;
            if (target == null || SelectedDockConfig(grid, target.Value, dockUid) == null) throw new InvalidOperationException("Шаттл не помещается у выбранного шлюза.");
            if (!slot.Paid)
            {
                if (HasComp<Content.Shared._Mono.Traits.Physical.IronmanComponent>(context.Actor)) throw new InvalidOperationException("Этот персонаж не может оплачивать выкуп.");
                var balance = await _database.PayParkingAsync(context.Session.UserId, context.Character, ParkingRules.RedemptionPrice(snapshot.OriginalPrice), slot.Redemption);
                _store.MarkPaid(user, slotId);
                if (_preferences.TryGetCachedPreferences(context.Session.UserId, out var prefs) && prefs.Characters.TryGetValue(context.Character, out var character)
                    && character is HumanoidCharacterProfile profile)
                    await _preferences.SetProfile(context.Session.UserId, context.Character, profile.WithBankBalance(balance), persist: false);
                if (Exists(context.Actor)) _bank.SetConfirmedBankBalance(context.Actor, balance);
            }
            if (epoch != _epoch || GetContext(context.Console, context.Actor)?.Card != context.Card || OwnedShip(context) != null || !Exists(grid))
                throw new InvalidOperationException("Условия изменились. Оплата учтена; повторите выкуп тем же персонажем.");
            restored.OwnerName = Name(context.Actor);
            _shipyard.BindParkingDeed(grid, context.Card, snapshot.Name, restored.OwnerName);
            Deliver(context, grid, dock);
            // Synchronous transition before the next game tick can expose the restored grid to clients.
            _store.CompleteRedeem(user, slotId);
            _activeShips[snapshot.Identity] = grid;
        }
        catch
        {
            if (TryComp<ShuttleDeedComponent>(context.Card, out var deed) && deed.ShuttleUid == grid) RemComp<ShuttleDeedComponent>(context.Card);
            if (Exists(grid)) Del(grid);
            throw;
        }
    }
}
