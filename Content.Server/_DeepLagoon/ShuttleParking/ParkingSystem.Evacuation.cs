using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Ghost;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private SharedMoverController _mover = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SleepingSystem _sleeping = default!;
    [Dependency] private TurfSystem _turf = default!;

    private const float ParkingClearance = 4f;
    private const float ArrivalRange = 0.2f;
    private static readonly TimeSpan EvacuationTimeout = TimeSpan.FromMinutes(1);

    private enum EvacuationLeg { Approach, Cross, Clear }

    private sealed record ParkingEvacuation(
        EntityUid Passenger,
        EntityUid ShipDock,
        EntityUid StationDock,
        EntityCoordinates Destination)
    {
        public EvacuationLeg Leg;
        public NPCSteeringComponent? Steering;
        public TimeSpan WalkDeadline;
        public bool TransferRequired;
    }

    private void StartParkingEvacuation(EntityUid grid, AutoParkingState state, EntityUid passenger, TimeSpan now)
    {
        state.Evacuation = FindParkingEvacuation(grid, state.Station, passenger);
        if (state.Evacuation == null)
        {
            AutoParkingStage(grid, state, "timer expired; waiting for a safe landing tile near a station dock");
            return;
        }

        AdvanceParkingEvacuation(grid, state, now);
    }

    private ParkingEvacuation? FindParkingEvacuation(EntityUid grid, EntityUid station, EntityUid passenger)
    {
        var from = _transform.GetWorldPosition(passenger);
        ParkingEvacuation? fallback = null;
        var docks = _docking.GetDocks(grid).OrderBy(dock => Vector2.DistanceSquared(from, _transform.GetWorldPosition(dock.Owner)));
        foreach (var dock in docks)
        {
            if (dock.Comp.DockedWith is not { } stationDock || !Exists(stationDock)
                || _stations.GetOwningStation(stationDock) != station || FindParkingClearance(stationDock) is not { } destination)
                continue;

            var evacuation = new ParkingEvacuation(passenger, dock.Owner, stationDock, destination);
            if (IsOpenParkingAirlock(dock.Owner) && IsOpenParkingAirlock(stationDock))
                return evacuation;

            fallback ??= evacuation;
        }

        return fallback;
    }

    private void AdvanceParkingEvacuation(EntityUid grid, AutoParkingState state, TimeSpan now)
    {
        var evacuation = state.Evacuation!;
        var passenger = evacuation.Passenger;
        if (TerminatingOrDeleted(passenger))
        {
            StopParkingEvacuation(state);
            return;
        }

        if (!IsOfflineParkingPassenger(passenger))
        {
            ResetAutoParking(grid, state, "cancelled: passenger is no longer eligible for offline evacuation");
            return;
        }

        if (CompOrNull<DockingComponent>(evacuation.ShipDock)?.DockedWith != evacuation.StationDock || !Exists(evacuation.StationDock))
        {
            ResetAutoParking(grid, state, "cancelled: the evacuation airlocks disconnected");
            return;
        }

        if (evacuation.TransferRequired || !IsOpenParkingAirlock(evacuation.ShipDock) || !IsOpenParkingAirlock(evacuation.StationDock))
        {
            TransferParkingPassenger(grid, state, evacuation);
            return;
        }

        if (evacuation.Steering is { } steering)
        {
            var waypoint = GetParkingWaypoint(evacuation);
            var xform = Transform(passenger);
            if (xform.GridUid == waypoint.EntityId && _transform.InRange(xform.Coordinates, waypoint, ArrivalRange))
            {
                if (evacuation.Leg == EvacuationLeg.Clear)
                {
                    StopParkingEvacuation(state);
                    AutoParkingStage(grid, state, "passenger clear of the docking port");
                    return;
                }

                evacuation.Leg++;
                SetParkingWaypoint(evacuation, steering);
                evacuation.WalkDeadline = now + EvacuationTimeout;
                AutoParkingStage(grid, state, evacuation.Leg == EvacuationLeg.Cross
                    ? "offline passenger crossing the paired airlocks"
                    : "offline passenger walking clear of the station port");
                return;
            }

            if (steering.Status == SteeringStatus.NoPath || now >= evacuation.WalkDeadline)
                TransferParkingPassenger(grid, state, evacuation);

            return;
        }

        if (!TryStartParkingWalk(evacuation))
        {
            TransferParkingPassenger(grid, state, evacuation);
            return;
        }

        evacuation.WalkDeadline = now + EvacuationTimeout;
        AutoParkingStage(grid, state, "timer expired; offline passenger walking to the ship port");
    }

    private void TransferParkingPassenger(EntityUid grid, AutoParkingState state, ParkingEvacuation evacuation)
    {
        evacuation.TransferRequired = true;
        StopParkingWalk(evacuation);
        var passenger = evacuation.Passenger;
        var destination = FindParkingClearance(evacuation.StationDock)
            ?? FindParkingEvacuation(grid, state.Station, passenger)?.Destination;
        if (destination is not { } landing || !ReleaseParkingPassenger(passenger))
        {
            AutoParkingStage(grid, state, "waiting: no safe transfer for the offline passenger");
            return;
        }

        if (state.Evacuation != evacuation || !IsOfflineParkingPassenger(passenger))
            return;

        _transform.SetCoordinates(passenger, landing);
        if (TryComp<PhysicsComponent>(passenger, out var body))
        {
            _physics.SetLinearVelocity(passenger, Vector2.Zero, body: body);
            _physics.SetAngularVelocity(passenger, 0, body: body);
        }

        StopParkingEvacuation(state);
        AutoParkingStage(grid, state, "walking unavailable; offline passenger transferred clear of the station port");
    }

    private bool TryStartParkingWalk(ParkingEvacuation evacuation)
    {
        var passenger = evacuation.Passenger;
        if (HasComp<ActorComponent>(passenger) || _mobState.IsIncapacitated(passenger) || HasComp<ActiveNPCComponent>(passenger)
            || HasComp<NPCSteeringComponent>(passenger) || !TryComp<InputMoverComponent>(passenger, out var mover)
            || !HasComp<PhysicsComponent>(passenger))
            return false;

        _sleeping.TryWaking(passenger, force: true);
        if (!ReleaseParkingPassenger(passenger) || !mover.CanMove || HasComp<RelayInputMoverComponent>(passenger))
            return false;

        _mover.SetSprinting((passenger, mover), ushort.MaxValue, walking: true);
        var steering = _steering.Register(passenger, GetParkingWaypoint(evacuation));
        steering.Walk = true;
        steering.Flags = PathFlags.Interact;
        steering.Range = ArrivalRange;
        steering.DirectMove = evacuation.Leg == EvacuationLeg.Cross;
        evacuation.Steering = steering;
        return true;
    }

    private bool ReleaseParkingPassenger(EntityUid passenger)
    {
        if (TryComp<PullerComponent>(passenger, out var puller) && puller.Pulling is { } pulled
            && TryComp<PullableComponent>(pulled, out var pulledComp) && !_pulling.TryStopPull(pulled, pulledComp))
            return false;
        if (TryComp<PullableComponent>(passenger, out var pullable) && !_pulling.TryStopPull(passenger, pullable))
            return false;

        if (TryComp<BuckleComponent>(passenger, out var buckle) && buckle.Buckled)
            _buckle.Unbuckle((passenger, buckle), null);

        return _containers.TryRemoveFromContainer(passenger, force: true, out var wasInContainer) || !wasInContainer;
    }

    private EntityCoordinates GetParkingWaypoint(ParkingEvacuation evacuation) => evacuation.Leg switch
    {
        EvacuationLeg.Approach => Transform(evacuation.ShipDock).Coordinates,
        EvacuationLeg.Cross => Transform(evacuation.StationDock).Coordinates,
        _ => evacuation.Destination,
    };

    private void SetParkingWaypoint(ParkingEvacuation evacuation, NPCSteeringComponent steering)
    {
        _steering.Register(evacuation.Passenger, GetParkingWaypoint(evacuation), steering);
        steering.DirectMove = evacuation.Leg == EvacuationLeg.Cross;
        steering.Status = SteeringStatus.Moving;
        steering.FailedPathCount = 0;
    }

    private void StopParkingEvacuation(AutoParkingState state)
    {
        if (state.Evacuation is not { } evacuation)
            return;

        state.Evacuation = null;
        StopParkingWalk(evacuation);
    }

    private void StopParkingWalk(ParkingEvacuation evacuation)
    {
        var steering = evacuation.Steering;
        evacuation.Steering = null;
        var passenger = evacuation.Passenger;
        if (steering == null || TerminatingOrDeleted(passenger))
            return;

        if (TryComp<InputMoverComponent>(passenger, out var mover))
            _mover.SetSprinting((passenger, mover), ushort.MaxValue, walking: false);

        _steering.Unregister(passenger, steering);
    }

    private void OnAutoParkingPlayerAttached(PlayerAttachedEvent ev)
    {
        if (HasComp<GhostComponent>(ev.Entity))
            return;

        var occupiedGrid = Transform(ev.Entity).GridUid;
        foreach (var (grid, state) in _autoParking)
        {
            if (occupiedGrid == grid || state.Evacuation?.Passenger == ev.Entity)
                ResetAutoParking(grid, state, "cancelled: a player took control of a passenger");
        }
    }

    private bool IsOpenParkingAirlock(EntityUid dock) =>
        TryComp<DoorComponent>(dock, out var door) && door.State == DoorState.Open;

    private EntityCoordinates? FindParkingClearance(EntityUid dock)
    {
        var xform = Transform(dock);
        if (xform.GridUid is not { } grid || xform.MapUid is not { } map || !TryComp<MapGridComponent>(grid, out var gridComp))
            return null;

        var port = xform.LocalPosition;
        var inward = -xform.LocalRotation.ToWorldVec();
        var preferred = port + inward * 3.5f;
        var best = float.PositiveInfinity;
        EntityCoordinates? destination = null;
        foreach (var tile in _map.GetLocalTilesIntersecting(grid, gridComp, new Circle(port, ParkingClearance)))
        {
            var center = _turf.GetTileCenter(tile);
            var offset = center.Position - port;
            var distance = offset.LengthSquared();
            var score = Vector2.DistanceSquared(center.Position, preferred);
            if (distance < 9f || distance > ParkingClearance * ParkingClearance || Vector2.Dot(inward, offset) < 1f
                || score >= best || tile.Tile.IsEmpty || _turf.IsSpace(tile) || _turf.IsTileBlocked(tile, CollisionGroup.MobMask)
                || !_atmosphere.IsTileMixtureProbablySafe(grid, (map, null), tile.GridIndices))
                continue;

            best = score;
            destination = center;
        }

        return destination;
    }
}
