using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server._DeepLagoon.Mapping;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Content.Server.Atmos.Components;
using Content.Server.Bed.Components;
using Content.Server.Chat.Managers;
using Content.Server.Construction.Components;
using Content.Server.GameTicking.Events;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using static Content.Shared.Damage.DamageableSystem;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Apartment prototype. Runtime instances, visits and layout storage have separate lifetimes.</summary>
public sealed partial class ApartmentSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private SharedMapSystem _maps => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private ITileDefinitionManager _tiles = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IGameTiming _timing = default!;

    public sealed class Instance
    {
        public required NetUserId Owner;
        public required EntityUid Grid;
        public required Vector2 Position;
        public required ApartmentTemplatePrototype Template;
        public required ApartmentDelta Delta;
        public readonly Dictionary<string, EntityUid> Furniture = new();
        public readonly Dictionary<string, ApartmentMapFurniture> MapFurniture = new();
        public required ApartmentGuestAccess Guests;
        public bool Frozen;
        public TimeSpan IdleSince;
    }

    private sealed class Visit
    {
        public required Instance Apartment;
        public required EntityUid Body;
        public required EntityCoordinates Return;
        public bool ResidentialTransit;
        public readonly List<(EntityUid Item, string? Slot, string? Hand)> Gear = new();
        public EntityUid? Uniform;
    }

    [Dependency] private MapLoaderSystem _mapLoader = default!;

    private readonly ApartmentRoundNumbers _numbers = new();
    public int Number(NetUserId owner) => _numbers.Number(owner.UserId);
    public NetUserId? NumberOwner(int number) => _numbers.Owner(number) is { } owner ? new NetUserId(owner) : null;

    private readonly Dictionary<NetUserId, Instance> _instances = new();
    // Round metadata survives grid unloading without keeping empty grids alive.
    private readonly Dictionary<NetUserId, ApartmentGuestAccess> _guestAccess = new();
    private readonly Dictionary<NetUserId, string> _templates = new();
    private readonly Dictionary<NetUserId, Visit> _visits = new();
    private readonly Dictionary<NetUserId, Guid> _editors = new();
    private readonly Dictionary<NetUserId, TimeSpan> _lastApply = new();
    private IApartmentLayoutStore? _store;
    private EntityUid _activeMap;
    private EntityUid _sleepMap;
    private TimeSpan _nextUpdate;
    private const int ApartmentGap = 2;
    private const int ApartmentRowWidth = 192;

    public bool Enabled => _config.GetCVar(ApartmentCVars.Enabled);
    public IReadOnlyDictionary<NetUserId, Instance> Instances => _instances;
    public IReadOnlyDictionary<NetUserId, ApartmentGuestAccess> GuestPolicies => _guestAccess;

    // Invitations can be prepared on the bus without loading an apartment grid.
    public void EnsureGuestPolicy(NetUserId owner) => _guestAccess.TryAdd(owner, new ApartmentGuestAccess());

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ApartmentApplyEvent>(OnApply);
        SubscribeNetworkEvent<ApartmentCommerceRequest>(OnCommerceRequest);
        SubscribeNetworkEvent<ApartmentFurniturePurchaseEvent>(OnFurniturePurchase);
        SubscribeNetworkEvent<ApartmentShopRequest>(OpenShop);
        SubscribeNetworkEvent<ApartmentShopAction>(ShopAction);
        SubscribeNetworkEvent<ApartmentActionEvent>(OnAction);
        SubscribeLocalEvent<ApartmentTerminalComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<ApartmentResidentComponent, BeforeDamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<ApartmentGridComponent, MassDataChangedEvent>(OnGridMassChanged, after: new[] { typeof(AutomaticAtmosSystem) });
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => Cleanup());
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete, after: new[]
        {
            typeof(Content.Server._DeepLagoon.Loadouts.PersonalLoadoutSpawnSystem),
            typeof(Content.Server.StationRecords.Systems.StationRecordsSystem),
            typeof(Content.Server.Antag.AntagSelectionSystem),
        });
        _players.PlayerStatusChanged += OnStatus;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnStatus;
        _store?.Dispose();
        base.Shutdown();
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!Enabled || args.Silent || args.Player.AttachedEntity != args.Mob)
            return;

        _editors.Remove(args.Player.UserId);
        if (_lobbyEditors.Remove(args.Player.UserId))
            Result(args.Player, true, "", -1);

        if (!args.LateJoin || !HasComp<ApartmentLatejoinSpawnComponent>(args.Station))
            return;

        // Keep the normal arrival point and loadout for leaving the apartment.
        // A failed load leaves the player at their normal spawn.
        if (!Enter(args.Player, args.Player.UserId, out var message, spawnAtBed: true))
        {
            Log.Warning($"Could not spawn player {args.Player.UserId} in their apartment: {message}");
            return;
        }

        _chat.DispatchServerMessage(args.Player, Loc.GetString("dl-apartment-spawn-gear-deferred"));
    }

    private IApartmentLayoutStore Store => _store ??= new ApartmentLayoutStore(_resources.UserData.RootDir is { } root
        ? Path.Combine(root, "apartments-prototype.db") : ":memory:");

    private void EnsureMaps()
    {
        if (Exists(_activeMap)) return;
        _activeMap = CreateResidentialMap(false);
        _sleepMap = CreateResidentialMap(true);
    }

    private EntityUid CreateResidentialMap(bool paused)
    {
        var uid = _map.CreateMap(out var id);
        _map.SetAmbientLight(id, Color.White);
        var gravity = EnsureComp<GravityComponent>(uid);
        gravity.Enabled = gravity.Inherent = true;
        var gas = new GasMixture(2500) { Temperature = 293.15f };
        gas.SetMoles(Gas.Oxygen, 22);
        gas.SetMoles(Gas.Nitrogen, 82);
        _atmos.SetMapAtmosphere(uid, false, gas);
        _map.SetPaused(id, paused);
        return uid;
    }

    private ApartmentFurniturePrototype? Catalog(string id) =>
        _prototypes.TryIndex<ApartmentFurniturePrototype>(id, out var furniture) ? furniture : null;

    public bool Enter(ICommonSession session, NetUserId owner, out string message, string? templateId = null, EntityCoordinates? returnPoint = null, bool spawnAtBed = false)
    {
        message = "";
        if (!Enabled) { message = "Прототип отключён: cvar deeplagoon.apartments_enabled true"; return false; }
        if (_visits.ContainsKey(session.UserId)) { message = "Вы уже находитесь в квартире."; return false; }
        if (session.AttachedEntity is not { } body || !TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive)
        { message = "Для посещения нужен живой персонаж."; return false; }
        if (TryComp<BuckleComponent>(body, out var buckle) && buckle.Buckled)
        { message = "Сначала встаньте с сиденья."; return false; }
        templateId ??= _config.GetCVar(ApartmentCVars.DefaultTemplate);
        EnsureMaps();
        try { RefreshSelectedInstance(owner); }
        catch { message = "Сначала освободите предыдущую квартиру и дождитесь завершения поездки."; return false; }
        if (!_instances.TryGetValue(owner, out var instance))
        {
            if (_instances.Count + _retiring.Count >= Math.Max(1, _config.GetCVar(ApartmentCVars.LoadedLimit)))
            { message = "Достигнут лимит загруженных квартир. Подождите выгрузки пустых помещений."; return false; }
            try { instance = Create(owner, _templates.GetValueOrDefault(owner, templateId)); }
            catch (Exception e) { Log.Error($"Apartment load failed for {owner}: {e}"); message = "Не удалось загрузить квартиру; сохранение оставлено без изменений."; return false; }
            _instances.Add(owner, instance);
            _guestAccess[owner] = instance.Guests;
            _templates[owner] = instance.Template.ID;
        }
        if (_visits.Values.Count(v => v.Apartment == instance) >= instance.Template.MaxVisitors)
        { message = "В квартире больше нет мест."; return false; }
        if (returnPoint is { } returnCoordinates && !returnCoordinates.IsValid(EntityManager))
        { message = "Точка возвращения недоступна."; return false; }
        var visit = new Visit { Apartment = instance, Body = body, Return = returnPoint ?? Transform(body).Coordinates,
            ResidentialTransit = returnPoint != null };
        var originalCoordinates = Transform(body).Coordinates;
        try
        {
            DepositGear(visit);
            Wake(instance);
            EnsureComp<ApartmentResidentComponent>(body);
            _transform.SetCoordinates(body, ArrivalCoordinates(instance, spawnAtBed));
            _visits.Add(session.UserId, visit);
        }
        catch (Exception e)
        {
            Log.Error($"Apartment entry failed: {e}");
            RemComp<ApartmentResidentComponent>(body);
            if (originalCoordinates.IsValid(EntityManager)) _transform.SetCoordinates(body, originalCoordinates);
            RestoreGear(visit);
            message = "Переход отменён, снаряжение возвращено.";
            return false;
        }
        message = "Вы прибыли в квартиру. Терминал: обустройство или возвращение.";
        return true;
    }

    private EntityCoordinates ArrivalCoordinates(Instance instance, bool spawnAtBed)
    {
        var arrival = new Vector2(instance.Template.Arrival.X + .5f, instance.Template.Arrival.Y + .5f);
        var coordinates = new EntityCoordinates(instance.Grid, arrival);
        if (!spawnAtBed)
            return coordinates;

        var distance = float.PositiveInfinity;
        var beds = EntityQueryEnumerator<StrapComponent, TransformComponent, MetaDataComponent>();
        while (beds.MoveNext(out _, out var strap, out var transform, out var metadata))
        {
            if (transform.ParentUid != instance.Grid || !transform.Anchored || strap.BuckledEntities.Count > 0 ||
                metadata.EntityPrototype is not { } prototype ||
                !_prototypes.EnumerateParents(prototype, includeSelf: true).Any(parent => parent.ID == "Bed"))
                continue;
            var candidateDistance = Vector2.DistanceSquared(arrival, transform.LocalPosition);
            if (candidateDistance >= distance)
                continue;
            distance = candidateDistance;
            coordinates = transform.Coordinates;
        }
        return coordinates;
    }

    private Instance Create(NetUserId owner, string templateId) => CreateFresh(owner, templateId, false);

    private Instance CreateFresh(NetUserId owner, string templateId, bool ignoreSaved)
    {
        var saved = ignoreSaved ? null : SelectedLayout(owner);
        var template = _prototypes.Index<ApartmentTemplatePrototype>(saved?.Template ?? templateId);
        if (template.Width is < 5 or > 32 || template.Height is < 5 or > 32) throw new InvalidOperationException("Unsupported template bounds.");
        var delta = saved ?? ApartmentLayout.Diff(template, template.Furniture, 0);
        var layout = template.Map == null ? ApartmentLayout.Resolve(template, delta) : new List<ApartmentPlacement>();
        if (template.Map == null && ApartmentLayout.Validate(template, layout, Catalog, validateStock: false) is { } error) throw new InvalidOperationException(error);
        Entity<MapGridComponent> grid;
        if (template.Map is { } path)
        {
            if (!_mapLoader.TryLoadGridTracked(Comp<MapComponent>(_activeMap).MapId, new ResPath(path), out var loaded,
                    new Robust.Shared.EntitySerialization.DeserializationOptions { StoreYamlUids = true }))
                throw new InvalidOperationException("Cannot load apartment template map.");
            grid = loaded.Value;
            var bounds = grid.Comp.LocalAABB;
            if (bounds.Left < 0 || bounds.Bottom < 0 || bounds.Right > template.Width || bounds.Top > template.Height)
            {
                Del(grid);
                throw new InvalidOperationException("Apartment map must fit within its declared template bounds at the local origin.");
            }
        }
        else grid = _maps.CreateGridEntity(Comp<MapComponent>(_activeMap).MapId);
        EnsureComp<ApartmentGridComponent>(grid);
        EnsureComp<ApartmentProtectedGridComponent>(grid);
        // Grid initialization installs a black implicit roof. Apartments use constant
        // indoor illumination instead of simulated lamps or daylight shadows.
        var roof = EnsureComp<ImplicitRoofComponent>(grid);
        roof.Color = Color.White;
        Dirty(grid, roof);
        var position = FindFreePosition(template);
        var instance = new Instance { Owner = owner, Grid = grid.Owner, Position = position, Template = template, Delta = delta,
            Guests = _guestAccess.GetValueOrDefault(owner) ?? new ApartmentGuestAccess() };
        try
        {
            _transform.SetCoordinates(grid, new EntityCoordinates(_activeMap, position));
            if (template.Map == null)
            {
                var tile = new Tile(_tiles[template.Floor].TileId);
                var tiles = new List<(Vector2i, Tile)>();
                for (var x = 0; x < template.Width; x++)
                for (var y = 0; y < template.Height; y++) tiles.Add((new Vector2i(x, y), tile));
                _map.SetTiles(grid, tiles);
                for (var x = 0; x < template.Width; x++)
                for (var y = 0; y < template.Height; y++)
                    if (x == 0 || y == 0 || x == template.Width - 1 || y == template.Height - 1)
                        Spawn(template.Wall, new EntityCoordinates(grid, new Vector2(x + .5f, y + .5f)));
            }
            var hasTerminal = false;
            var terminals = EntityQueryEnumerator<ApartmentTerminalComponent, TransformComponent>();
            while (terminals.MoveNext(out var terminal, out _, out var transform))
            {
                if (transform.GridUid != grid.Owner)
                    continue;
                hasTerminal = true;
                RemComp<ConstructionComponent>(terminal);
            }
            if (!hasTerminal)
            {
                var terminal = Spawn("DLApartmentTerminal", new EntityCoordinates(grid, new Vector2(template.Arrival.X + .5f, template.Arrival.Y + 1.5f)));
                RemComp<ConstructionComponent>(terminal);
            }
            if (Available(template) && !EntityQuery<ApartmentElevatorComponent, TransformComponent>().Any(pair => pair.Item2.GridUid == grid.Owner && pair.Item2.Anchored))
                throw new InvalidOperationException("Apartment template must contain an anchored elevator panel.");
            if (template.Map != null) RegisterMapFurniture(instance);
            layout = ApartmentLayout.Resolve(template, delta, MapBaseline(instance));
            if (template.Map != null && ApartmentLayout.Validate(template, layout, id => Catalog(instance, id), CaptureRoom(instance), MapBaseline(instance), validateStock: false) is { } mapError)
                throw new InvalidOperationException(mapError);
            foreach (var item in layout)
                if (instance.MapFurniture.TryGetValue(item.Id, out var mapped))
                {
                    if (!item.SameAs(mapped.Baseline)) MoveMapFurniture(instance, item);
                }
                else instance.Furniture[item.Id] = SpawnFurniture(item, grid);
            return instance;
        }
        catch { Del(grid); throw; }
    }

    private Vector2 FindFreePosition(ApartmentTemplatePrototype template)
    {
        // Reserve frozen apartments too: waking them must restore the same free location.
        // Maps have no fixed edge, so extend the rows when the current ones are full.
        var columns = _instances.Values.Concat(_retiring).Select(i => i.Position.X + i.Template.Width + ApartmentGap)
            .Append(0f).Distinct().OrderBy(x => x).ToArray();
        var rows = _instances.Values.Concat(_retiring).Select(i => i.Position.Y + i.Template.Height + ApartmentGap)
            .Append(0f).Distinct().OrderBy(y => y);
        foreach (var y in rows)
        foreach (var x in columns)
        {
            if (x + template.Width > ApartmentRowWidth)
                continue;
            if (_instances.Values.Concat(_retiring).Any(i =>
                    x < i.Position.X + i.Template.Width + ApartmentGap &&
                    x + template.Width + ApartmentGap > i.Position.X &&
                    y < i.Position.Y + i.Template.Height + ApartmentGap &&
                    y + template.Height + ApartmentGap > i.Position.Y))
                continue;
            return new Vector2(x, y);
        }

        throw new InvalidOperationException("No free apartment position found.");
    }

    private EntityUid SpawnFurniture(ApartmentPlacement item, EntityUid parent)
    {
        var definition = _prototypes.Index<ApartmentFurniturePrototype>(item.Furniture);
        var entity = Spawn(definition.Entity, new EntityCoordinates(parent, new Vector2(item.X + .5f, item.Y + .5f)));
        // Prototype furniture cannot heal, be disassembled or dragged outside the editor.
        RemComp<ConstructionComponent>(entity);
        RemComp<HealOnBuckleComponent>(entity);
        RemComp<PullableComponent>(entity);
        _transform.SetLocalRotation(entity, Angle.FromDegrees(item.Rotation * 90));
        return entity;
    }

    public bool Invite(NetUserId owner, NetUserId guest)
    {
        return owner != guest && _guestAccess.TryGetValue(owner, out var access) && access.Invite(guest.UserId, _timing.RealTime);
    }

    public bool TryGetVisit(NetUserId user, out Instance instance, out EntityUid body)
    {
        instance = default!; body = default;
        if (!_visits.TryGetValue(user, out var visit)) return false;
        instance = visit.Apartment; body = visit.Body; return true;
    }

    public NetUserId[] Visitors(Instance instance) => _visits.Where(p => p.Value.Apartment == instance).Select(p => p.Key).ToArray();
    public bool UsesResidentialTransit(NetUserId user) => _visits.TryGetValue(user, out var visit) && visit.ResidentialTransit;
    public bool SetReturnPoint(NetUserId user, EntityCoordinates point)
    {
        if (!_visits.TryGetValue(user, out var visit) || !point.IsValid(EntityManager)) return false;
        visit.Return = point; return true;
    }

    public bool RequestLeave(NetUserId user, out string message)
    {
        var request = new ApartmentLeaveRequestedEvent(user);
        RaiseLocalEvent(request);
        if (request.Handled) { message = request.Message; return true; }
        return Leave(user, out message);
    }

    public bool Leave(NetUserId user, out string message)
    {
        message = "Вы не находитесь в квартире.";
        if (!_visits.TryGetValue(user, out var visit)) return false;
        if (!Exists(visit.Body) || !visit.Return.IsValid(EntityManager))
        { message = "Точка возвращения недоступна. Обратитесь к администратору."; return false; }
        if (TryComp<BuckleComponent>(visit.Body, out var buckle) && buckle.Buckled)
        { message = "Сначала встаньте с сиденья."; return false; }
        // Delete only the temporary clothes we issued; never clone or delete deposited gear.
        if (visit.Uniform is { } uniform && Exists(uniform)) Del(uniform);
        _transform.SetCoordinates(visit.Body, visit.Return);
        RemComp<ApartmentResidentComponent>(visit.Body);
        RestoreGear(visit);
        _visits.Remove(user);
        _editors.Remove(user);
        RefreshActivity(visit.Apartment);
        message = "Вы вернулись, сданное снаряжение выдано обратно.";
        return true;
    }

    private void DepositGear(Visit visit)
    {
        var body = visit.Body;
        if (TryComp<PullerComponent>(body, out var puller) && puller.Pulling is { } pulled && TryComp<PullableComponent>(pulled, out var pulledComp)) _pulling.TryStopPull(pulled, pulledComp);
        if (TryComp<PullableComponent>(body, out var bodyPullable)) _pulling.TryStopPull(body, bodyPullable);
        if (TryComp<InventoryComponent>(body, out var inventory))
        {
            // Unequipping a suit may also drop its dependent slots. Snapshot before any mutation.
            var equipped = new List<(EntityUid Item, string Slot)>();
            foreach (var slot in inventory.Slots)
                if (_inventory.TryGetSlotEntity(body, slot.Name, out var item)) equipped.Add((item.Value, slot.Name));
            foreach (var (item, slot) in equipped)
            {
                if (_inventory.TryGetSlotEntity(body, slot, out var current) && current == item &&
                    !_inventory.TryUnequip(body, slot, out _, silent: true, force: true)) throw new InvalidOperationException("Cannot deposit equipped item.");
                visit.Gear.Add((item, slot, null));
                _transform.SetCoordinates(item, new EntityCoordinates(_sleepMap, Vector2.Zero));
            }
        }
        if (TryComp<HandsComponent>(body, out var hands))
        foreach (var (name, hand) in hands.Hands)
        {
            if (hand.HeldEntity is not { } item) continue;
            if (!_hands.TryDrop(body, hand, checkActionBlocker: false, doDropInteraction: false)) throw new InvalidOperationException("Cannot deposit held item.");
            visit.Gear.Add((item, null, name));
            _transform.SetCoordinates(item, new EntityCoordinates(_sleepMap, Vector2.Zero));
        }
        // Preserve species-specific organs/implants; the prototype is restricted to local HOST testing.
        if (_inventory.HasSlot(body, "jumpsuit"))
        {
            var uniform = Spawn("ClothingUniformJumpsuitColorGrey", Transform(body).Coordinates);
            visit.Uniform = uniform;
            if (!_inventory.TryEquip(body, uniform, "jumpsuit", silent: true, force: true)) Del(uniform);
        }
    }

    private void RestoreGear(Visit visit)
    {
        if (visit.Uniform is { } uniform && Exists(uniform)) Del(uniform);
        foreach (var (item, slot, hand) in visit.Gear)
        {
            if (!Exists(item)) continue;
            _transform.SetCoordinates(item, Transform(visit.Body).Coordinates);
            if (slot != null) _inventory.TryEquip(visit.Body, item, slot, silent: true, force: true);
            else _hands.TryPickup(visit.Body, item, hand, checkActionBlocker: false, animate: false);
        }
        visit.Gear.Clear();
    }

    public bool OpenEditor(ICommonSession session, EntityUid? terminal = null)
    {
        if (!Enabled || !_visits.TryGetValue(session.UserId, out var visit) || visit.Apartment.Owner != session.UserId || session.AttachedEntity != visit.Body ||
            HasComp<ApartmentTripPendingComponent>(visit.Body) || Transform(visit.Body).GridUid != visit.Apartment.Grid) return false;
        var instance = visit.Apartment;
        if (Store.Load(session.UserId.UserId) is { } selected && selected.Template != instance.Template.ID)
        {
            PublishShop(session, "Сначала спуститесь на остановку и поднимитесь в выбранную квартиру по новому номеру.");
            return false;
        }
        if (terminal is { } console && (!HasComp<ApartmentTerminalComponent>(console) || Transform(console).GridUid != instance.Grid)) return false;
        var token = Guid.NewGuid();
        _editors[session.UserId] = token;
        RaiseNetworkEvent(new ApartmentEditorEvent(GetNetEntity(instance.Grid), instance.Template.ID, instance.Delta.Revision, token,
            CurrentLayout(instance), instance.Furniture.Where(p => Exists(p.Value) && Transform(p.Value).ParentUid == instance.Grid)
                .ToDictionary(p => p.Key, p => GetNetEntity(p.Value)),
            CaptureRoom(instance), Number(instance.Owner), instance.MapFurniture.Values.ToList(), ownedFurniture: _economy.Cached(session.UserId)?.State.Furniture.ToArray()), session);
        return true;
    }

    private void OnVerbs(Entity<ApartmentTerminalComponent> terminal, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !TryComp<ActorComponent>(args.User, out var actor) ||
            !_visits.TryGetValue(actor.PlayerSession.UserId, out var visit) || Transform(terminal).GridUid != visit.Apartment.Grid) return;
        var session = actor.PlayerSession;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-apartment-exit"), Act = () => { RequestLeave(session.UserId, out var message); Result(session, false, message, _visits.ContainsKey(session.UserId) ? visit.Apartment.Delta.Revision : -1); } });
        if (visit.Apartment.Owner == session.UserId)
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-apartment-edit"), Act = () => OpenEditor(session, terminal.Owner) });
    }

    private void OnAction(ApartmentActionEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (ev.Action == "close") { _editors.Remove(session.UserId); _lobbyEditors.Remove(session.UserId); _commerceRequests.Remove(session.UserId); return; }
        if (ev.Action == "exit") { RequestLeave(session.UserId, out var message); Result(session, false, message, _visits.TryGetValue(session.UserId, out var visit) ? visit.Apartment.Delta.Revision : -1); }
    }

    private void OnApply(ApartmentApplyEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var mapCount = _visits.TryGetValue(session.UserId, out var current) ? current.Apartment.MapFurniture.Count : _lobbyEditors.GetValueOrDefault(session.UserId)?.MapFurniture.Count ?? 0;
        if (ev.Layout == null || ev.Layout.Count > ApartmentLayout.SafetyLimit || ev.Layout.Any(p => p == null))
        { Result(session, false, "Некорректный проект квартиры.", -1); return; }
        var error = Apply(session, ev.Token, ev.Revision, ev.Layout);
        var revision = _visits.TryGetValue(session.UserId, out var visit) ? visit.Apartment.Delta.Revision : _lobbyEditors.GetValueOrDefault(session.UserId)?.Delta.Revision ?? -1;
        Result(session, error == null, error ?? "Обстановка сохранена.", revision);
        if (error == null) { if (InLobby(session)) OpenLobbyEditor(session); else OpenEditor(session); }
    }

    public string? Apply(ICommonSession session, Guid token, int revision, IReadOnlyList<ApartmentPlacement> layout)
    {
        if (!Enabled || !_editors.TryGetValue(session.UserId, out var expected) || token != expected) return "Нет доступа к редактированию квартиры.";
        var lobby = InLobby(session) && _lobbyEditors.ContainsKey(session.UserId);
        _visits.TryGetValue(session.UserId, out var visit);
        var instance = lobby ? _lobbyEditors[session.UserId] : visit?.Apartment;
        if (instance == null || instance.Owner != session.UserId || !lobby && (visit == null || session.AttachedEntity != visit.Body || Transform(visit.Body).GridUid != instance.Grid))
            return "Нет доступа к редактированию квартиры.";
        if (Store.Load(session.UserId.UserId) is { } selected && selected.Template != instance.Template.ID) return "Шаблон квартиры изменён. Откройте обустройство заново.";
        if (!lobby && HasComp<ApartmentTripPendingComponent>(visit!.Body)) return "Дождитесь завершения поездки.";
        if (revision != instance.Delta.Revision) return "Обстановка изменилась. Откройте редактор заново.";
        if (_lastApply.TryGetValue(session.UserId, out var last) && _timing.RealTime - last < TimeSpan.FromMilliseconds(500)) return "Подождите перед повторным сохранением.";
        _lastApply[session.UserId] = _timing.RealTime;
        var account = _economy.Cached(session.UserId);
        if (ApartmentLayout.Validate(instance.Template, layout, id => Catalog(instance, id), CaptureRoom(instance), MapBaseline(instance),
                stock: id => FurnitureCapacity(id, account)) is { } error) return error;
        var old = CurrentLayout(instance).ToDictionary(p => p.Id);
        var touched = layout.Where(p => !old.TryGetValue(p.Id, out var previous) || !p.SameAs(previous)).ToList();
        if (instance.MapFurniture.Keys.Any(id => !instance.Furniture.TryGetValue(id, out var entity) || !Exists(entity)))
            return "Исходный предмет квартиры удалён. Откройте квартиру заново после её перезагрузки.";
        if (touched.Any(p => instance.Furniture.TryGetValue(p.Id, out var entity) && TryComp<StrapComponent>(entity, out var strap) && strap.BuckledEntities.Count > 0))
            return "Перед перестановкой освободите мебель от пассажиров.";
        if (!lobby && TryComp<BuckleComponent>(visit!.Body, out var buckle) && buckle.Buckled) return "Перед применением встаньте с сиденья.";
        foreach (var participant in _visits.Values.Where(v => v.Apartment == instance && Exists(v.Body)))
        {
            if (participant.Body != visit?.Body && TryComp<BuckleComponent>(participant.Body, out var otherBuckle) && otherBuckle.Buckled)
                return "Посетитель сидит на мебели. Попросите его встать.";
            var pos = Transform(participant.Body).LocalPosition;
            var cell = new Vector2i((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
            if (touched.Any(p => ApartmentLayout.Cells(p, Catalog(instance, p.Furniture)!).Contains(cell))) return "Посетитель находится на месте новой мебели.";
        }
        var prepared = new Dictionary<string, EntityUid>();
        var delta = ApartmentLayout.Diff(instance.Template, layout, revision + 1, MapBaseline(instance));
        var committed = false;
        try
        {
            foreach (var p in touched.Where(p => !instance.MapFurniture.ContainsKey(p.Id))) prepared[p.Id] = SpawnFurniture(p, _sleepMap);
            if (!Store.Save(session.UserId.UserId, delta, revision)) return "Сохранение изменилось. Откройте квартиру заново.";
            committed = true;
        }
        catch (Exception e)
        {
            Log.Error($"Apartment save failed: {e}");
            return "Не удалось сохранить обстановку. Черновик можно повторить.";
        }
        finally
        {
            // Prepared objects are retained only after the durable revision commits.
            if (!committed)
                foreach (var entity in prepared.Values) if (Exists(entity)) Del(entity);
        }
        foreach (var (id, entity) in instance.Furniture.ToArray())
            if (layout.All(p => p.Id != id) || prepared.ContainsKey(id)) { Del(entity); instance.Furniture.Remove(id); }
        foreach (var p in touched)
        {
            if (instance.MapFurniture.ContainsKey(p.Id)) { MoveMapFurniture(instance, p); continue; }
            var entity = prepared[p.Id];
            _transform.SetCoordinates(entity, new EntityCoordinates(instance.Grid, new Vector2(p.X + .5f, p.Y + .5f)));
            _transform.AnchorEntity(entity);
            instance.Furniture[p.Id] = entity;
        }
        instance.Delta = delta;
        return null;
    }

    private void Result(ICommonSession session, bool success, string message, int revision) =>
        RaiseNetworkEvent(new ApartmentResultEvent(success, message, revision), session);

    private void OnDamage(Entity<ApartmentResidentComponent> entity, ref BeforeDamageChangedEvent args) => args.Cancelled = true;

    // AutomaticAtmosSystem normally creates mutable vacuum tiles on any large grid.
    // Residential grids always breathe the map's immutable mixture, including after mass changes.
    private void OnGridMassChanged(Entity<ApartmentGridComponent> entity, ref MassDataChangedEvent args)
    {
        if (TryComp<Content.Shared._DeepLagoon.Mapping.GridEnvironmentComponent>(entity, out var environment) && environment.SafeAtmosphere)
            _atmos.SetSafeGridAtmosphere(entity);
        else RemComp<GridAtmosphereComponent>(entity);
    }

    private void OnStatus(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus == SessionStatus.Disconnected)
        {
            _editors.Remove(args.Session.UserId);
            _lobbyEditors.Remove(args.Session.UserId);
            _shopTokens.Remove(args.Session.UserId);
        }
        if (!_visits.TryGetValue(args.Session.UserId, out var visit)) return;
        RefreshActivity(visit.Apartment);
    }

    private void Wake(Instance instance)
    {
        if (!instance.Frozen) return;
        _transform.SetCoordinates(instance.Grid, new EntityCoordinates(_activeMap, instance.Position));
        instance.Frozen = false;
    }

    public void RefreshActivity(Instance instance)
    {
        if (HasComp<ApartmentElevatorReservedComponent>(instance.Grid)) { Wake(instance); return; }
        var active = _visits.Any(p => p.Value.Apartment == instance && _players.TryGetSessionById(p.Key, out var session) &&
            session.Status == SessionStatus.InGame && session.AttachedEntity == p.Value.Body);
        if (active) { Wake(instance); return; }
        if (instance.Frozen) return;
        _transform.SetCoordinates(instance.Grid, new EntityCoordinates(_sleepMap, instance.Position));
        instance.Frozen = true;
        instance.IdleSince = _timing.RealTime;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.RealTime < _nextUpdate || _instances.Count == 0 && _retiring.Count == 0) return;
        _nextUpdate = _timing.RealTime + TimeSpan.FromSeconds(1);
        foreach (var instance in _instances.Values.Concat(_retiring).ToArray())
        {
            if (_lobbyEditors.TryGetValue(instance.Owner, out var editing) && editing == instance && _editors.ContainsKey(instance.Owner) &&
                _players.TryGetSessionById(instance.Owner, out var viewer) && InLobby(viewer)) continue;
            RefreshActivity(instance);
            if (HasComp<ApartmentElevatorReservedComponent>(instance.Grid) || !instance.Frozen || _visits.Values.Any(v => v.Apartment == instance) ||
                _timing.RealTime - instance.IdleSince < TimeSpan.FromSeconds(Math.Max(1, _config.GetCVar(ApartmentCVars.EmptyTimeout)))) continue;
            Del(instance.Grid);
            if (_instances.GetValueOrDefault(instance.Owner) == instance) _instances.Remove(instance.Owner);
            _retiring.Remove(instance);
        }
    }

    private void Cleanup()
    {
        _numbers.Reset();
        foreach (var visit in _visits.Values)
            if (Exists(visit.Body) && visit.Return.IsValid(EntityManager))
            { _transform.SetCoordinates(visit.Body, visit.Return); RemComp<ApartmentResidentComponent>(visit.Body); RestoreGear(visit); }
        _commerceRequests.Clear();
        _visits.Clear(); _instances.Clear(); _editors.Clear(); _lastApply.Clear(); _guestAccess.Clear(); _templates.Clear(); _shopTokens.Clear(); _lobbyEditors.Clear(); _retiring.Clear();
        if (Exists(_activeMap)) Del(_activeMap);
        if (Exists(_sleepMap)) Del(_sleepMap);
        _activeMap = _sleepMap = EntityUid.Invalid;
    }
}
