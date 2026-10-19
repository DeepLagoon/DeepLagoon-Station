using System.Linq;
using Content.Server._Mono.FireControl;
using Content.Server.Atmos.Components;
using Content.Server.Chat.Systems;
using Content.Server.Destructible;
using Content.Server.Power.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared.Chat;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleConsoleSystem
{
    private readonly Dictionary<EntityUid, ShuttleHullBaseline> _hullBaselines = new();
    private readonly Dictionary<EntityUid, ShuttleHullState> _hullStates = new();
    private readonly Dictionary<EntityUid, ShuttleHullLossAlert> _hullAlerts = new();
    private readonly HashSet<EntityUid> _dirtyHullGrids = new();
    private readonly HashSet<EntityUid> _pendingHullBroadcasts = new();
    [Dependency] private ChatSystem _hullChat = default!;
    [Dependency] private ThrusterSystem _hullThrusters = default!;
    private TimeSpan _nextHullUpdate;

    private void InitializeHullUi()
    {
        // Start recording when the ship exists, even if nobody has opened its console yet.
        SubscribeLocalEvent<ShuttleComponent, MapInitEvent>(OnHullMapInit);
        SubscribeLocalEvent<ShuttleComponent, EntityTerminatingEvent>(OnHullShutdown);
        SubscribeLocalEvent<TransformComponent, MapInitEvent>(OnHullEntityMapInit);
        SubscribeLocalEvent<TransformComponent, AnchorStateChangedEvent>(OnHullEntityAnchor);
        SubscribeLocalEvent<TransformComponent, EntityTerminatingEvent>(OnHullEntityTerminating);
        SubscribeLocalEvent<TileChangedEvent>(OnHullTileChanged);
    }

    private void OnHullMapInit(EntityUid uid, ShuttleComponent component, MapInitEvent args)
    {
        if (CaptureHull(uid) is { } state)
        {
            _hullStates[uid] = state;
        }
    }

    private void OnHullShutdown(EntityUid uid, ShuttleComponent component, ref EntityTerminatingEvent args)
    {
        _hullBaselines.Remove(uid);
        _hullStates.Remove(uid);
        _hullAlerts.Remove(uid);
        _dirtyHullGrids.Remove(uid);
        _pendingHullBroadcasts.Remove(uid);
    }

    private void OnHullEntityMapInit(EntityUid uid, TransformComponent component, MapInitEvent args) => RecordHullEntity(uid);

    private void OnHullEntityAnchor(EntityUid uid, TransformComponent component, ref AnchorStateChangedEvent args) => RecordHullEntity(uid);

    private void OnHullEntityTerminating(EntityUid uid, TransformComponent component, ref EntityTerminatingEvent args)
    {
        if (component.GridUid is { } grid && HasComp<ShuttleComponent>(grid) && HullEntityKind(uid) != 0)
            _dirtyHullGrids.Add(grid);
    }

    private ShuttleHullBaseline GetHullBaseline(EntityUid grid)
    {
        if (!_hullBaselines.TryGetValue(grid, out var baseline))
            _hullBaselines.Add(grid, baseline = new ShuttleHullBaseline());
        return baseline;
    }

    private void OnHullTileChanged(ref TileChangedEvent args)
    {
        if (!HasComp<ShuttleComponent>(args.Entity))
            return;
        var baseline = GetHullBaseline(args.Entity);
        _dirtyHullGrids.Add(args.Entity);
        foreach (var change in args.Changes)
        {
            if (!change.NewTile.IsEmpty)
                baseline.Record(change.GridIndices, ShuttleHullBaseline.Floor);
        }
    }

    private byte HullEntityKind(EntityUid uid)
    {
        if (HasComp<ThrusterComponent>(uid))
            return ShuttleHullBaseline.Thruster;
        if (HasComp<FireControllableComponent>(uid))
            return ShuttleHullBaseline.Weapon;
        // Walls, windows and doors remain represented even if they lack a destructible component.
        if (HasComp<DestructibleComponent>(uid) || HasComp<AirtightComponent>(uid))
            return ShuttleHullBaseline.Structure;
        return 0;
    }

    private void RecordHullEntity(EntityUid uid)
    {
        var xform = Transform(uid);
        if (xform.GridUid is not { } grid || !HasComp<ShuttleComponent>(grid)
            || !TryComp<MapGridComponent>(grid, out var mapGrid))
            return;
        var kind = HullEntityKind(uid);
        if (kind != 0)
        {
            _dirtyHullGrids.Add(grid);
            if (xform.Anchored)
                GetHullBaseline(grid).Record(_mapSystem.GetTileRef(grid, mapGrid, xform.Coordinates).GridIndices, kind);
        }
    }

    private ShuttleHullState? GetHullState(EntityUid? grid)
    {
        if (grid is not { } uid || !HasComp<ShuttleComponent>(uid))
            return null;
        if (_hullStates.TryGetValue(uid, out var cached) && !_dirtyHullGrids.Contains(uid))
            return cached;
        var state = CaptureHull(uid);
        if (state == null)
            return null;
        _dirtyHullGrids.Remove(uid);
        _hullStates[uid] = state;
        // A normal console refresh may populate the cache before the broadcast interval. Other
        // open consoles still need this update even when the next scan matches the new cache.
        if (cached == null || !HullStatesEqual(cached, state))
            _pendingHullBroadcasts.Add(uid);
        return state;
    }

    private ShuttleHullState? CaptureHull(EntityUid grid)
    {
        if (!TryComp<MapGridComponent>(grid, out var mapGrid))
            return null;
        var current = new Dictionary<(Vector2i Tile, byte Kind), int>();
        var workingThrusters = 0;
        var workingGyroscopes = 0;
        var storedEnergy = 0f;
        var energyCapacity = 0f;
        foreach (var tile in _mapSystem.GetAllTiles(grid, mapGrid))
        {
            if (!tile.Tile.IsEmpty)
                current[(tile.GridIndices, ShuttleHullBaseline.Floor)] = 1;
        }

        // Walk just this grid's children instead of querying every destructible in the world per ship.
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            var xform = Transform(child);
            if (!xform.Anchored || TerminatingOrDeleted(child))
                continue;
            if (TryComp<ThrusterComponent>(child, out var thruster) && _hullThrusters.CanEnable(child, thruster))
            {
                if (thruster.Type == ThrusterType.Angular)
                    workingGyroscopes++;
                else
                    workingThrusters++;
            }
            // Fixed power-network storage only: SMES, substations and APCs. Pocket cells and gun batteries are excluded.
            if (HasComp<PowerNetworkBatteryComponent>(child) && TryComp<BatteryComponent>(child, out var battery))
            {
                storedEnergy += battery.CurrentCharge;
                energyCapacity += battery.MaxCharge;
            }
            var kind = HullEntityKind(child);
            if (kind == 0)
                continue;
            var tile = _mapSystem.GetTileRef(grid, mapGrid, xform.Coordinates).GridIndices;
            var key = (tile, kind);
            current[key] = current.GetValueOrDefault(key) + 1;
        }

        var baseline = GetHullBaseline(grid);
        var state = baseline.Build(current);
        state.WorkingThrusters = workingThrusters;
        state.WorkingGyroscopes = workingGyroscopes;
        state.StoredEnergy = storedEnergy;
        state.EnergyCapacity = energyCapacity;
        ObserveHullLosses(grid, state, baseline.NewLosses);
        return state;
    }

    private static bool HullStatesEqual(ShuttleHullState left, ShuttleHullState right)
    {
        return left.Width == right.Width && left.Height == right.Height
            && left.ExpectedThrusters == right.ExpectedThrusters && left.MissingThrusters == right.MissingThrusters
            && left.ExpectedWeapons == right.ExpectedWeapons && left.MissingWeapons == right.MissingWeapons
            && left.ExpectedStructures == right.ExpectedStructures && left.MissingStructures == right.MissingStructures
            && left.ExpectedTiles == right.ExpectedTiles && left.MissingTiles == right.MissingTiles
            && left.WorkingThrusters == right.WorkingThrusters && left.WorkingGyroscopes == right.WorkingGyroscopes
            && left.StoredEnergy == right.StoredEnergy && left.EnergyCapacity == right.EnergyCapacity
            && left.Cells.SequenceEqual(right.Cells);
    }

    private void ProcessHullUiUpdates()
    {
        if (_shieldTiming.CurTime < _nextHullUpdate)
            return;
        _nextHullUpdate = _shieldTiming.CurTime + TimeSpan.FromSeconds(2);
        var changed = new HashSet<EntityUid>(_pendingHullBroadcasts);
        _pendingHullBroadcasts.Clear();
        var grids = EntityQueryEnumerator<ShuttleComponent, MapGridComponent>();
        while (grids.MoveNext(out var uid, out _, out _))
        {
            if (CaptureHull(uid) is not { } state)
                continue;
            _dirtyHullGrids.Remove(uid);
            if (!_hullStates.TryGetValue(uid, out var old) || !HullStatesEqual(old, state))
            {
                _hullStates[uid] = state;
                changed.Add(uid);
            }
        }

        var announced = new HashSet<EntityUid>();
        var consoles = EntityQueryEnumerator<ShuttleConsoleComponent>();
        while (consoles.MoveNext(out var uid, out _))
        {
            var ev = new ConsoleShuttleEvent { Console = uid };
            RaiseLocalEvent(uid, ref ev);
            if (ev.Console is not { } console || Transform(console).GridUid is not { } grid)
                continue;
            if (changed.Contains(grid) && _ui.IsUiOpen(uid, ShuttleConsoleUiKey.Key))
                _ui.ServerSendUiMessage(uid, ShuttleConsoleUiKey.Key, new ShuttleHullStateMessage(_hullStates[grid]));
            if (_hullAlerts.TryGetValue(grid, out var alert) && alert.Ready(_shieldTiming.CurTime)
                && Transform(uid).Anchored && this.IsPowered(uid, EntityManager))
            {
                AnnounceHullLosses(uid, alert);
                announced.Add(grid);
            }
        }

        foreach (var grid in announced)
            _hullAlerts[grid].Acknowledge(_shieldTiming.CurTime);
    }

    private void ObserveHullLosses(EntityUid grid, ShuttleHullState state, ShuttleHullState losses)
    {
        if (!_hullAlerts.TryGetValue(grid, out var alert))
            _hullAlerts.Add(grid, alert = new ShuttleHullLossAlert());
        alert.ObserveNewLosses(state, losses);
    }

    private void AnnounceHullLosses(EntityUid console, ShuttleHullLossAlert alert)
    {
        var message = Loc.GetString("shuttle-hull-alert-damage", ("percent", Math.Max(1, alert.DamagePercent)));

        _hullChat.TrySendInGameICMessage(console, message, InGameICChatType.Speak, hideChat: false);
    }
}
