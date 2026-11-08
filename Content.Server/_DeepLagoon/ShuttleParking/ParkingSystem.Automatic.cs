using System.Linq;
using Content.Server.EUI;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;

    private static readonly TimeSpan AutoParkingDelay = TimeSpan.FromMinutes(30);
    private readonly Dictionary<EntityUid, AutoParkingState> _autoParking = [];
    private TimeSpan _nextAutoParkingCheck;

    private sealed record AutoParkingState(EntityUid Station, NetUserId Owner)
    {
        public TimeSpan? ReadySince;
        public string? LastStage;
        public ParkingEvacuation? Evacuation;
    }

    private void UpdateAutoParking(TimeSpan now)
    {
        if (now < _nextAutoParkingCheck)
            return;

        _nextAutoParkingCheck = now + TimeSpan.FromSeconds(1);
        var docked = new Dictionary<EntityUid, EntityUid>();
        var stations = EntityQueryEnumerator<StationAutomaticShuttleParkingComponent, StationDataComponent>();
        while (stations.MoveNext(out var station, out _, out var data))
        {
            foreach (var stationGrid in data.Grids)
            {
                foreach (var dock in _docking.GetDocks(stationGrid))
                {
                    if (dock.Comp.DockedWith is not { } other || !TryComp(other, out TransformComponent? transform)
                        || transform.GridUid is not { } ship || ship == stationGrid || !HasComp<ShuttleComponent>(ship)
                        || _stations.GetOwningStation(ship) == station)
                        continue;

                    docked.TryAdd(ship, station);
                }
            }
        }

        foreach (var (grid, state) in _autoParking.ToArray())
        {
            if (docked.GetValueOrDefault(grid) == state.Station)
                continue;

            AutoParkingStage(grid, state, "cancelled: no longer docked to the station");
            StopParkingEvacuation(state);
            _autoParking.Remove(grid);
        }

        foreach (var (grid, station) in docked)
            CheckAutoParking(grid, station, now);
    }

    private void CheckAutoParking(EntityUid grid, EntityUid station, TimeSpan now)
    {
        var owner = GetAutoParkingOwner(grid);
        if (owner == null)
        {
            if (_autoParking.Remove(grid, out var inactive))
                StopParkingEvacuation(inactive);
            return;
        }

        if (!_autoParking.TryGetValue(grid, out var state) || state.Owner != owner || state.Station != station)
        {
            if (state != null)
                StopParkingEvacuation(state);
            _autoParking[grid] = state = new(station, owner.Value);
            AutoParkingStage(grid, state, "eligible dock detected");
        }

        var occupants = new List<EntityUid>();
        if (IsOnline(owner.Value))
            ResetAutoParking(grid, state, "waiting: owner online");
        else if (_stations.GetOwningStation(grid) is { } shipStation && _players.Sessions.Any(player =>
                     IsOnline(player) && _stationJobs.TryGetPlayerJobs(shipStation, player.UserId, out _)))
            ResetAutoParking(grid, state, "waiting: assigned crew online");
        else if (HasComp<FTLComponent>(grid))
            ResetAutoParking(grid, state, "waiting: FTL in progress");
        else if (!CollectOfflineOccupants(grid, occupants))
            ResetAutoParking(grid, state, "waiting: online passenger or unrecognized mind aboard");
        else
            AdvanceAutoParking(grid, state, occupants, now);
    }

    private void AdvanceAutoParking(EntityUid grid, AutoParkingState state, List<EntityUid> occupants, TimeSpan now)
    {
        var debugSeconds = _configuration.GetCVar(ShuttleParkingCVars.AutoDebugSeconds);
        var delay = debugSeconds > 0 ? TimeSpan.FromSeconds(debugSeconds) : AutoParkingDelay;
        if (state.ReadySince == null)
        {
            state.ReadySince = now;
            AutoParkingStage(grid, state, $"timer started ({delay.TotalSeconds:0} seconds)");
        }

        if (now - state.ReadySince.Value < delay)
            return;

        if (state.Evacuation != null)
        {
            // The occupancy list predates this update and may still include a transferred passenger.
            AdvanceParkingEvacuation(grid, state, now);
            return;
        }

        if (occupants.Count > 0)
        {
            StartParkingEvacuation(grid, state, occupants[0], now);
            return;
        }

        var ownerName = CompOrNull<ShuttleDeedComponent>(grid)?.ShuttleOwner ?? string.Empty;
        if (ResolveOwnedShip(grid, state.Owner, ownerName) is not { } ship)
        {
            ResetAutoParking(grid, state, "waiting: could not resolve ship ownership");
            return;
        }

        AutoParkingStage(grid, state, "timer expired; no players remain aboard");
        // Undocking during ParkShuttle must not be treated as cancellation.
        _autoParking.Remove(grid);
        try
        {
            ParkShuttle(grid, ship.ParkingCharacter);
            ship.AutoParkingNoticePending = true;
            AutoParkingStage(grid, state, "parked; owner notice queued");
        }
        catch (Exception e)
        {
            Log.Error($"Automatic shuttle parking failed for {ToPrettyString(grid)}: {e}");
        }
    }

    private NetUserId? GetAutoParkingOwner(EntityUid grid)
    {
        if (TerminatingOrDeleted(grid))
            return null;

        if (TryComp<ParkingShuttleComponent>(grid, out var ship))
            return !ship.Parked && _activeShips.GetValueOrDefault(ship.Identity) == grid ? new NetUserId(ship.OwnerAccount) : null;

        if (TryComp<ShipOwnershipComponent>(grid, out var ownership) && ownership.OwnerUserId != default
            && TryComp<VesselComponent>(grid, out var vessel) && _prototypes.HasIndex(vessel.VesselId))
            return ownership.OwnerUserId;

        return null;
    }

    private bool CollectOfflineOccupants(EntityUid grid, List<EntityUid> occupants)
    {
        foreach (var entity in Descendants(grid))
        {
            if (HasComp<GhostComponent>(entity))
                continue;

            if (HasComp<ActorComponent>(entity))
                return false;

            if (!TryComp<MindContainerComponent>(entity, out var container) || !container.HasMind)
                continue;

            if (!IsOfflineParkingPassenger(entity, container))
                return false;

            occupants.Add(entity);
        }

        return true;
    }

    private bool IsOfflineParkingPassenger(EntityUid entity, MindContainerComponent? container = null) =>
        !HasComp<ActorComponent>(entity)
        && _mind.TryGetMind(entity, out _, out var mind, container)
        && mind.UserId is { } user
        && !IsOnline(user);

    private bool IsOnline(NetUserId user) => _players.TryGetSessionById(user, out var session) && IsOnline(session);

    private static bool IsOnline(ICommonSession session) => session.Status is not (SessionStatus.Disconnected or SessionStatus.Zombie);

    private void OnAutoParkingPlayerStatus(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Connected || _autoParking.Count == 0)
            return;

        var user = args.Session.UserId;
        _mind.TryGetMind(user, out _, out var mind);
        var body = mind?.OwnedEntity;
        var occupiedGrid = TryComp(body, out TransformComponent? transform) ? transform.GridUid : null;
        foreach (var (grid, state) in _autoParking)
        {
            if (state.Owner == user || occupiedGrid == grid || state.Evacuation is { } evacuation && evacuation.Passenger == body
                || _stations.GetOwningStation(grid) is { } shipStation
                && _stationJobs.TryGetPlayerJobs(shipStation, user, out _))
                ResetAutoParking(grid, state, "cancelled: owner or crew reconnected");
        }
    }

    private void OnAutoParkingUndock(UndockEvent ev)
    {
        CancelIfUndocked(ev.GridAUid);
        CancelIfUndocked(ev.GridBUid);
    }

    private void CancelIfUndocked(EntityUid grid)
    {
        if (!_autoParking.TryGetValue(grid, out var state))
            return;

        if (state.Evacuation is { } evacuation && CompOrNull<DockingComponent>(evacuation.ShipDock)?.DockedWith != evacuation.StationDock)
            ResetAutoParking(grid, state, "cancelled: evacuation airlocks disconnected");

        if (_docking.GetDocks(grid).Any(dock =>
                dock.Comp.DockedWith is { } other && Exists(other) && _stations.GetOwningStation(other) == state.Station))
            return;

        AutoParkingStage(grid, state, "cancelled: last station dock disconnected");
        _autoParking.Remove(grid);
    }

    private void ResetAutoParking(EntityUid grid, AutoParkingState state, string stage)
    {
        StopParkingEvacuation(state);
        state.ReadySince = null;
        AutoParkingStage(grid, state, stage);
    }

    private void ClearAutoParking()
    {
        foreach (var state in _autoParking.Values)
            StopParkingEvacuation(state);

        _autoParking.Clear();
        _nextAutoParkingCheck = TimeSpan.Zero;
    }

    private void AutoParkingStage(EntityUid grid, AutoParkingState state, string stage)
    {
        if (state.LastStage == stage)
            return;

        state.LastStage = stage;
        if (AutoParkingDebugEnabled)
            Log.Info($"Auto parking {ToPrettyString(grid)} at {ToPrettyString(state.Station)}: {stage}.");
    }

    private bool AutoParkingDebugEnabled =>
        _configuration.GetCVar(ShuttleParkingCVars.AutoDebug) || _configuration.GetCVar(ShuttleParkingCVars.AutoDebugSeconds) > 0;

    internal void NotifyAutoParkedOwner(ICommonSession session)
    {
        var notified = false;
        foreach (var grid in _parkingMaps.Keys)
        {
            if (!TryComp<ParkingShuttleComponent>(grid, out var ship) || !ship.Parked
                || ship.OwnerAccount != session.UserId.UserId || !ship.AutoParkingNoticePending)
                continue;

            if (!notified)
                _eui.OpenEui(new AutoParkingNoticeEui(), session);

            notified = true;
            ship.AutoParkingNoticePending = false;
            if (AutoParkingDebugEnabled)
                Log.Info($"Auto parking {ToPrettyString(grid)}: owner joined the game; notice sent.");
        }
    }
}
