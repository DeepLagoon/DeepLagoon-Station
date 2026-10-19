using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Components;
using Content.Server.Bed.Components;
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
    [Dependency] private IGameTiming _timing = default!;

    public sealed class Instance
    {
        public required NetUserId Owner;
        public required EntityUid Grid;
        public required Vector2 Position;
        public required ApartmentTemplatePrototype Template;
        public required ApartmentDelta Delta;
        public readonly Dictionary<string, EntityUid> Furniture = new();
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

    private readonly Dictionary<NetUserId, Instance> _instances = new();
    // Small round metadata survives grid unloading. Invitations never keep empty grids alive.
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
        if (!Enabled || !args.LateJoin || args.Player.AttachedEntity != args.Mob)
            return;

        // Keep the normal arrival point and loadout for leaving the apartment.
        // A failed load leaves the player at their normal latejoin spawn.
        if (!Enter(args.Player, args.Player.UserId, out var message))
            Log.Warning($"Could not spawn latejoining player {args.Player.UserId} in their apartment: {message}");
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

    public bool Enter(ICommonSession session, NetUserId owner, out string message, string templateId = "DLApartmentStudio", EntityCoordinates? returnPoint = null)
    {
        message = "";
        if (!Enabled) { message = "Прототип отключён: cvar deeplagoon.apartments_enabled true"; return false; }
        if (_visits.ContainsKey(session.UserId)) { message = "Вы уже находитесь в квартире."; return false; }
        if (session.AttachedEntity is not { } body || !TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive)
        { message = "Для посещения нужен живой персонаж."; return false; }
        if (TryComp<BuckleComponent>(body, out var buckle) && buckle.Buckled)
        { message = "Сначала встаньте с сиденья."; return false; }
        EnsureMaps();
        if (!_instances.TryGetValue(owner, out var instance))
        {
            if (owner != session.UserId && (!_guestAccess.TryGetValue(owner, out var access) || !access.MayEnter(session.UserId.UserId, _timing.RealTime)))
            { message = "Приглашение отсутствует или истекло."; return false; }
            if (_instances.Count >= Math.Max(1, _config.GetCVar(ApartmentCVars.LoadedLimit)))
            { message = "Достигнут лимит загруженных квартир. Подождите выгрузки пустых помещений."; return false; }
            try { instance = Create(owner, _templates.GetValueOrDefault(owner, templateId)); }
            catch (Exception e) { Log.Error($"Apartment load failed for {owner}: {e}"); message = "Не удалось загрузить квартиру; сохранение оставлено без изменений."; return false; }
            _instances.Add(owner, instance);
            _guestAccess[owner] = instance.Guests;
            _templates[owner] = instance.Template.ID;
        }
        if (owner != session.UserId && !instance.Guests.MayEnter(session.UserId.UserId, _timing.RealTime))
        { message = "Хозяин не приглашал вас в эту квартиру."; return false; }
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
            _transform.SetCoordinates(body, new EntityCoordinates(instance.Grid, new Vector2(instance.Template.Arrival.X + .5f, instance.Template.Arrival.Y + .5f)));
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

    private Instance Create(NetUserId owner, string templateId)
    {
        var saved = Store.Load(owner.UserId);
        var template = _prototypes.Index<ApartmentTemplatePrototype>(saved?.Template ?? templateId);
        if (template.Width is < 5 or > 32 || template.Height is < 5 or > 32) throw new InvalidOperationException("Unsupported template bounds.");
        var delta = saved ?? ApartmentLayout.Diff(template, template.Furniture, 0);
        var layout = ApartmentLayout.Resolve(template, delta);
        if (ApartmentLayout.Validate(template, layout, Catalog) is { } error) throw new InvalidOperationException(error);
        var grid = _maps.CreateGridEntity(Comp<MapComponent>(_activeMap).MapId);
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
            var tile = new Tile(_tiles[template.Floor].TileId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < template.Width; x++)
            for (var y = 0; y < template.Height; y++) tiles.Add((new Vector2i(x, y), tile));
            _map.SetTiles(grid, tiles);
            for (var x = 0; x < template.Width; x++)
            for (var y = 0; y < template.Height; y++)
                if (x == 0 || y == 0 || x == template.Width - 1 || y == template.Height - 1)
                    Spawn(template.Wall, new EntityCoordinates(grid, new Vector2(x + .5f, y + .5f)));
            var terminal = Spawn("DLApartmentTerminal", new EntityCoordinates(grid, new Vector2(template.Arrival.X + .5f, template.Arrival.Y + 1.5f)));
            RemComp<ConstructionComponent>(terminal);
            foreach (var item in layout) instance.Furniture[item.Id] = SpawnFurniture(item, grid);
            return instance;
        }
        catch { Del(grid); throw; }
    }

    private Vector2 FindFreePosition(ApartmentTemplatePrototype template)
    {
        // Reserve frozen apartments too: waking them must restore the same free location.
        // Maps have no fixed edge, so extend the rows when the current ones are full.
        var columns = _instances.Values.Select(i => i.Position.X + i.Template.Width + ApartmentGap)
            .Append(0f).Distinct().OrderBy(x => x).ToArray();
        var rows = _instances.Values.Select(i => i.Position.Y + i.Template.Height + ApartmentGap)
            .Append(0f).Distinct().OrderBy(y => y);
        foreach (var y in rows)
        foreach (var x in columns)
        {
            if (x + template.Width > ApartmentRowWidth)
                continue;
            if (_instances.Values.Any(i =>
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

    public bool OpenEditor(ICommonSession session)
    {
        if (!Enabled || !_visits.TryGetValue(session.UserId, out var visit) || visit.Apartment.Owner != session.UserId || session.AttachedEntity != visit.Body ||
            HasComp<ApartmentTripPendingComponent>(visit.Body) || Transform(visit.Body).GridUid != visit.Apartment.Grid) return false;
        var instance = visit.Apartment;
        var token = Guid.NewGuid();
        _editors[session.UserId] = token;
        RaiseNetworkEvent(new ApartmentEditorEvent(GetNetEntity(instance.Grid), instance.Template.ID, instance.Delta.Revision, token,
            ApartmentLayout.Resolve(instance.Template, instance.Delta), instance.Furniture.ToDictionary(p => p.Key, p => GetNetEntity(p.Value))), session);
        return true;
    }

    private void OnVerbs(Entity<ApartmentTerminalComponent> terminal, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !TryComp<ActorComponent>(args.User, out var actor) ||
            !_visits.TryGetValue(actor.PlayerSession.UserId, out var visit) || Transform(terminal).GridUid != visit.Apartment.Grid) return;
        var session = actor.PlayerSession;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-apartment-exit"), Act = () => { RequestLeave(session.UserId, out var message); Result(session, false, message, _visits.ContainsKey(session.UserId) ? visit.Apartment.Delta.Revision : -1); } });
        if (visit.Apartment.Owner == session.UserId)
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("dl-apartment-edit"), Act = () => OpenEditor(session) });
    }

    private void OnAction(ApartmentActionEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (ev.Action == "close") { _editors.Remove(session.UserId); return; }
        if (ev.Action == "exit") { RequestLeave(session.UserId, out var message); Result(session, false, message, _visits.TryGetValue(session.UserId, out var visit) ? visit.Apartment.Delta.Revision : -1); }
    }

    private void OnApply(ApartmentApplyEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (ev.Layout == null || ev.Layout.Count > 64 || ev.Layout.Any(p => p == null))
        { Result(session, false, "Некорректный проект квартиры.", -1); return; }
        var error = Apply(session, ev.Token, ev.Revision, ev.Layout);
        var revision = _visits.TryGetValue(session.UserId, out var visit) ? visit.Apartment.Delta.Revision : -1;
        Result(session, error == null, error ?? "Обстановка сохранена.", revision);
        if (error == null) OpenEditor(session);
    }

    public string? Apply(ICommonSession session, Guid token, int revision, IReadOnlyList<ApartmentPlacement> layout)
    {
        if (!Enabled || !_editors.TryGetValue(session.UserId, out var expected) || token != expected ||
            !_visits.TryGetValue(session.UserId, out var visit) || visit.Apartment.Owner != session.UserId ||
            session.AttachedEntity != visit.Body || Transform(visit.Body).GridUid != visit.Apartment.Grid)
            return "Нет доступа к редактированию квартиры.";
        var instance = visit.Apartment;
        if (HasComp<ApartmentTripPendingComponent>(visit.Body)) return "Дождитесь завершения поездки.";
        if (revision != instance.Delta.Revision) return "Обстановка изменилась. Откройте редактор заново.";
        if (_lastApply.TryGetValue(session.UserId, out var last) && _timing.RealTime - last < TimeSpan.FromMilliseconds(500)) return "Подождите перед повторным сохранением.";
        _lastApply[session.UserId] = _timing.RealTime;
        if (ApartmentLayout.Validate(instance.Template, layout, Catalog) is { } error) return error;
        var old = ApartmentLayout.Resolve(instance.Template, instance.Delta).ToDictionary(p => p.Id);
        var touched = layout.Where(p => !old.TryGetValue(p.Id, out var previous) || !p.SameAs(previous)).ToList();
        if (TryComp<BuckleComponent>(visit.Body, out var buckle) && buckle.Buckled) return "Перед применением встаньте с сиденья.";
        foreach (var participant in _visits.Values.Where(v => v.Apartment == instance && Exists(v.Body)))
        {
            if (participant.Body != visit.Body && TryComp<BuckleComponent>(participant.Body, out var otherBuckle) && otherBuckle.Buckled)
                return "Посетитель сидит на мебели. Попросите его встать.";
            var pos = Transform(participant.Body).LocalPosition;
            var cell = new Vector2i((int) MathF.Floor(pos.X), (int) MathF.Floor(pos.Y));
            if (touched.Any(p => ApartmentLayout.Cells(p, Catalog(p.Furniture)!).Contains(cell))) return "Посетитель находится на месте новой мебели.";
        }
        var prepared = new Dictionary<string, EntityUid>();
        var delta = ApartmentLayout.Diff(instance.Template, layout, revision + 1);
        var committed = false;
        try
        {
            foreach (var p in touched) prepared[p.Id] = SpawnFurniture(p, _sleepMap);
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
    private void OnGridMassChanged(Entity<ApartmentGridComponent> entity, ref MassDataChangedEvent args) =>
        RemComp<GridAtmosphereComponent>(entity);

    private void OnStatus(object? sender, SessionStatusEventArgs args)
    {
        if (!_visits.TryGetValue(args.Session.UserId, out var visit)) return;
        if (args.NewStatus == SessionStatus.Disconnected) _editors.Remove(args.Session.UserId);
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
        if (_timing.RealTime < _nextUpdate || _instances.Count == 0) return;
        _nextUpdate = _timing.RealTime + TimeSpan.FromSeconds(1);
        foreach (var instance in _instances.Values.ToArray())
        {
            RefreshActivity(instance);
            if (!instance.Frozen || _visits.Values.Any(v => v.Apartment == instance) ||
                _timing.RealTime - instance.IdleSince < TimeSpan.FromSeconds(Math.Max(1, _config.GetCVar(ApartmentCVars.EmptyTimeout)))) continue;
            Del(instance.Grid);
            _instances.Remove(instance.Owner);
        }
    }

    private void Cleanup()
    {
        foreach (var visit in _visits.Values)
            if (Exists(visit.Body) && visit.Return.IsValid(EntityManager))
            { _transform.SetCoordinates(visit.Body, visit.Return); RemComp<ApartmentResidentComponent>(visit.Body); RestoreGear(visit); }
        _visits.Clear(); _instances.Clear(); _editors.Clear(); _lastApply.Clear(); _guestAccess.Clear(); _templates.Clear();
        if (Exists(_activeMap)) Del(_activeMap);
        if (Exists(_sleepMap)) Del(_sleepMap);
        _activeMap = _sleepMap = EntityUid.Invalid;
    }
}
