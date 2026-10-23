using System.Linq;
using Content.Server._NF.PublicTransit;
using Content.Server._NF.PublicTransit.Components;
using Content.Server.Popups;
using Content.Server.Shuttles.Components;
using Content.Server.Doors.Systems;
using Content.Server.Construction.Completions;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Transport adapter for the existing bus. Private rooms never become public bus stops.</summary>
public sealed partial class ApartmentTravelSystem : EntitySystem
{
    [Dependency] private ApartmentSystem _apartments = default!;
    [Dependency] private PublicTransitSystem _transit = default!;
    [Dependency] private DoorSystem _doors = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private PopupSystem _popup = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private ApartmentProtectionSystem _protection = default!;

    private sealed class Context
    {
        public required Guid Token;
        public required EntityUid Terminal, Body;
        public TimeSpan Expires, NextAction;
    }

    private sealed class Trip
    {
        public required Guid Ticket;
        public required NetUserId User, Owner;
        public required EntityUid Body, Terminal;
        public EntityUid? Stop;
        public bool Returning;
        public TimeSpan Expires;
        public DoAfterId? DoAfter;
    }

    private readonly Dictionary<NetUserId, Context> _contexts = new();
    private readonly Dictionary<NetUserId, Trip> _trips = new();
    private readonly Dictionary<NetUserId, EntityUid> _returnStops = new();
    private readonly HashSet<EntityUid> _addedStops = new();
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentTerminalComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<ApartmentEntranceComponent, ApartmentEntranceActivatedEvent>(OnEntranceActivate);
        SubscribeLocalEvent<ApartmentEntranceComponent, ConstructionBeforeDeleteEvent>((uid, comp, args) => args.Cancel());
        SubscribeNetworkEvent<ApartmentTravelActionEvent>(OnAction);
        SubscribeLocalEvent<ApartmentTripDoAfterEvent>(OnTrip);
        SubscribeLocalEvent<ApartmentLeaveRequestedEvent>(OnLeaveRequested);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => Cleanup());
        _players.PlayerStatusChanged += OnStatus;
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnStatus;
        base.Shutdown();
    }

    private void OnActivate(Entity<ApartmentTerminalComponent> terminal, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex || !TryComp<ActorComponent>(args.User, out var actor)) return;
        args.Handled = Open(actor.PlayerSession, terminal);
    }

    private void OnEntranceActivate(EntityUid uid, ApartmentEntranceComponent comp, ApartmentEntranceActivatedEvent args)
    {
        if (TryComp<ActorComponent>(args.User, out var actor)) Open(actor.PlayerSession, uid);
    }

    private bool CanUse(ICommonSession session, EntityUid terminal)
    {
        if (!_apartments.Enabled || session.Status != SessionStatus.InGame || session.AttachedEntity is not { } body || Deleted(terminal) ||
            !TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive) return false;
        if (HasComp<ApartmentEntranceComponent>(terminal))
            return _interaction.InRangeUnobstructed(body, terminal) && Transform(body).GridUid == Transform(terminal).GridUid && TryBoardingStop(terminal, out _);
        return _interaction.InRangeUnobstructed(body, terminal) && HasComp<ApartmentTerminalComponent>(terminal) && _apartments.TryGetVisit(session.UserId, out var apartment, out var resident) &&
            resident == body && Transform(body).GridUid == apartment.Grid && Transform(terminal).GridUid == apartment.Grid;
    }

    private bool TryBoardingStop(EntityUid terminal, out EntityUid stop)
    {
        stop = default;
        if (Deleted(terminal) || !HasComp<ApartmentEntranceComponent>(terminal) ||
            !Transform(terminal).Anchored || Transform(terminal).GridUid is not { } entranceGrid) return false;
        var query = EntityQueryEnumerator<ApartmentTransitStopComponent, TransformComponent>();
        while (query.MoveNext(out var marker, out _, out var transform))
        {
            if (!ValidStop(marker) || transform.GridUid != entranceGrid) continue;
            stop = marker; return true;
        }
        return false;
    }

    internal bool ValidStop(EntityUid marker)
    {
        if (Deleted(marker) || !HasComp<ApartmentTransitStopComponent>(marker) || MetaData(marker).EntityPaused) return false;
        var transform = Transform(marker);
        return transform.Anchored && transform.GridUid is { } grid && transform.ParentUid == grid && !Deleted(grid) &&
            TryComp<MapGridComponent>(grid, out var mapGrid) && !HasComp<ApartmentGridComponent>(grid) && !HasComp<TransitShuttleComponent>(grid) &&
            !_map.GetTileRef(grid, mapGrid, transform.Coordinates).Tile.IsEmpty;
    }

    public bool Open(ICommonSession session, EntityUid terminal)
    {
        if (!CanUse(session, terminal)) return false;
        if (HasComp<ApartmentEntranceComponent>(terminal)) _apartments.EnsureGuestPolicy(session.UserId);
        _contexts[session.UserId] = new Context { Token = Guid.NewGuid(), Terminal = terminal, Body = session.AttachedEntity!.Value,
            Expires = _timing.RealTime + TimeSpan.FromMinutes(2) };
        Publish(session); return true;
    }

    private void Publish(ICommonSession session, string message = "")
    {
        if (!_contexts.TryGetValue(session.UserId, out var context)) return;
        var atHome = _apartments.TryGetVisit(session.UserId, out var home, out _);
        var canManage = atHome && home.Owner == session.UserId;
        var people = new List<ApartmentTravelPerson>();
        var invitations = new List<ApartmentTravelPerson>();
        RaiseNetworkEvent(new ApartmentTravelStateEvent(context.Token, atHome, canManage, true,
            !atHome && TryBoardingStop(context.Terminal, out _), _trips.ContainsKey(session.UserId), people, invitations, message), session);
    }

    private void OnAction(ApartmentTravelActionEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_contexts.TryGetValue(session.UserId, out var context) || ev.Token != context.Token) return;
        if (ev.Action == ApartmentTravelAction.Close) { Close(session); return; }
        if (context.Expires <= _timing.RealTime || session.AttachedEntity != context.Body || !CanUse(session, context.Terminal)) { Close(session); return; }
        if (_timing.RealTime < context.NextAction) return;
        context.NextAction = _timing.RealTime + TimeSpan.FromMilliseconds(200);
        context.Expires = _timing.RealTime + TimeSpan.FromMinutes(2);
        if (_trips.ContainsKey(session.UserId)) { Publish(session, Loc.GetString("dl-apartment-trip-pending")); return; }
        var target = new NetUserId(ev.Target);
        var message = "";
        switch (ev.Action)
        {
            case ApartmentTravelAction.Refresh: break;
            case ApartmentTravelAction.Depart: message = Begin(session, context.Terminal, session.UserId, false); break;
            case ApartmentTravelAction.Visit: message = Begin(session, context.Terminal, target, false); break;
            case ApartmentTravelAction.Return: message = Begin(session, context.Terminal, session.UserId, true); break;
            case ApartmentTravelAction.Edit:
                if (_apartments.OpenEditor(session, context.Terminal)) Close(session);
                else message = Loc.GetString("dl-apartment-owner-only");
                break;
            default: message = Loc.GetString("dl-apartment-owner-only"); break;
        }
        Publish(session, message);
    }

    private string Begin(ICommonSession session, EntityUid terminal, NetUserId owner, bool returning)
    {
        if (Transform(terminal).GridUid is { } grid && HasElevator(grid)) return "Воспользуйтесь пультом лифта.";
        if (!CanUse(session, terminal) || session.AttachedEntity is not { } body) return Loc.GetString("dl-apartment-terminal-required");
        if (_trips.ContainsKey(session.UserId)) return Loc.GetString("dl-apartment-trip-pending");
        if (TryComp<BuckleComponent>(body, out var buckle) && buckle.Buckled) return Loc.GetString("dl-apartment-unbuckle");
        EntityUid? stop = null;
        if (returning)
        {
            if (!_apartments.TryGetVisit(session.UserId, out _, out var resident) || resident != body || !PrepareReturn(session.UserId))
                return Loc.GetString("dl-apartment-return-unavailable");
        }
        else
        {
            if (_apartments.TryGetVisit(session.UserId, out _, out _) || !TryBoardingStop(terminal, out var boarding)) return Loc.GetString("dl-apartment-boarding-required");
            stop = boarding;

        }
        var trip = new Trip { Ticket = Guid.NewGuid(), User = session.UserId, Owner = owner, Body = body, Terminal = terminal,
            Stop = stop, Returning = returning, Expires = _timing.RealTime + TimeSpan.FromSeconds(30) };
        var doAfter = new DoAfterArgs(EntityManager, body, 3f, new ApartmentTripDoAfterEvent { Ticket = trip.Ticket }, null, target: terminal)
        { Broadcast = true, BreakOnMove = true, BreakOnDamage = true, MovementThreshold = .3f, NeedHand = false, MultiplyDelay = false };
        if (!_doAfter.TryStartDoAfter(doAfter, out var doAfterId)) return Loc.GetString("dl-apartment-trip-denied");
        trip.DoAfter = doAfterId;
        _trips[session.UserId] = trip;
        EnsureComp<ApartmentTripPendingComponent>(body);
        return Loc.GetString(returning ? "dl-apartment-trip-returning" : "dl-apartment-trip-departing");
    }

    private void OnTrip(ApartmentTripDoAfterEvent ev)
    {
        if (ev.Handled) return;
        var trip = _trips.Values.FirstOrDefault(t => t.Ticket == ev.Ticket);
        if (trip == null) return;
        ev.Handled = true;
        CancelTrip(trip.User, false);
        if (!_players.TryGetSessionById(trip.User, out var session)) return;
        var message = Loc.GetString("dl-apartment-trip-cancelled");
        if (!ev.Cancelled && session.AttachedEntity == trip.Body && CanUse(session, trip.Terminal))
        {
            bool success;
            if (trip.Returning)
                success = PrepareReturn(trip.User) && _apartments.Leave(trip.User, out message);
            else if (trip.Stop is { } stop && TryBoardingStop(trip.Terminal, out var current) && current == stop)
            {
                success = _apartments.Enter(session, trip.Owner, out message, returnPoint: Transform(stop).Coordinates);
                if (success) _returnStops[trip.User] = stop;
            }
            else success = false;
            if (success) Close(session);
        }
        _popup.PopupCursor(message, session);
        Publish(session, message);
    }

    public bool ReturnLegacy(ICommonSession session) => _apartments.TryGetVisit(session.UserId, out var apartment, out _)
        && apartment.Template.Map == null && PrepareReturn(session.UserId) && _apartments.Leave(session.UserId, out _);

    private bool PrepareReturn(NetUserId user)
    {
        if (!_apartments.UsesResidentialTransit(user)) return true;
        if (_returnStops.TryGetValue(user, out var original) && ValidStop(original))
            return _apartments.SetReturnPoint(user, Transform(original).Coordinates);
        var query = EntityQueryEnumerator<ApartmentTransitStopComponent>();
        while (query.MoveNext(out var marker, out _))
            if (ValidStop(marker)) return _apartments.SetReturnPoint(user, Transform(marker).Coordinates);
        return false;
    }

    private void OnLeaveRequested(ApartmentLeaveRequestedEvent ev)
    {
        if (_apartments.TryGetVisit(ev.User, out var apartment, out _) && HasElevator(apartment.Grid))
        {
            ev.Handled = true;
            ev.Message = "Для возвращения воспользуйтесь пультом лифта.";
            return;
        }
        if (!_apartments.UsesResidentialTransit(ev.User)) return;
        ev.Handled = true;
        if (!_apartments.Enabled)
        {
            if (PrepareReturn(ev.User)) _apartments.Leave(ev.User, out ev.Message);
            else ev.Message = Loc.GetString("dl-apartment-return-unavailable");
            return;
        }
        ev.Message = Loc.GetString("dl-apartment-terminal-required");
        if (!_players.TryGetSessionById(ev.User, out var session) || !_apartments.TryGetVisit(ev.User, out var home, out _)) return;
        var query = EntityQueryEnumerator<ApartmentTerminalComponent, TransformComponent>();
        while (query.MoveNext(out var terminal, out _, out var transform))
            if (transform.GridUid == home.Grid && CanUse(session, terminal)) { ev.Message = Begin(session, terminal, ev.User, true); return; }
    }

    private void Close(ICommonSession session)
    {
        if (_contexts.Remove(session.UserId)) RaiseNetworkEvent(new ApartmentTravelClosedEvent(), session);
    }

    private void CancelTrip(NetUserId user, bool cancelDoAfter = true)
    {
        if (!_trips.Remove(user, out var trip) || Deleted(trip.Body)) return;
        RemComp<ApartmentTripPendingComponent>(trip.Body);
        if (cancelDoAfter && _doAfter.IsRunning(trip.DoAfter)) _doAfter.Cancel(trip.DoAfter);
    }

    private void OnStatus(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus != SessionStatus.Disconnected) return;
        _contexts.Remove(args.Session.UserId); CancelTrip(args.Session.UserId);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.RealTime < _nextUpdate) return;
        _nextUpdate = _timing.RealTime + TimeSpan.FromSeconds(1);
        var hubs = new HashSet<EntityUid>();
        if (_apartments.Enabled)
        {
            var markers = EntityQueryEnumerator<ApartmentTransitStopComponent, TransformComponent>();
            while (markers.MoveNext(out var marker, out _, out var transform))
                if (ValidStop(marker) && transform.GridUid is { } grid) hubs.Add(grid);
            foreach (var grid in hubs) if (_transit.RegisterStop(grid)) _addedStops.Add(grid);
            if (hubs.Count > 0) _transit.EnsureService();
            var airlocks = EntityQueryEnumerator<AirlockComponent, TransformComponent>();
            while (airlocks.MoveNext(out var airlock, out _, out var transform))
            {
                if (!transform.Anchored || transform.GridUid is not { } grid || !hubs.Contains(grid) ||
                    HasComp<DockingComponent>(airlock)) continue;
                if (HasElevator(grid)) { RemComp<ApartmentEntranceComponent>(airlock); continue; }
                EnsureComp<ApartmentEntranceComponent>(airlock);
                _doors.SetState(airlock, DoorState.Closed);
            }
        }
        _protection.SetStops(hubs);
        var entrances = EntityQueryEnumerator<ApartmentEntranceComponent, TransformComponent>();
        while (entrances.MoveNext(out var entrance, out _, out var transform))
            if (!transform.Anchored || transform.GridUid is not { } grid || !hubs.Contains(grid) || HasComp<DockingComponent>(entrance))
                RemComp<ApartmentEntranceComponent>(entrance);
        foreach (var grid in _addedStops.Where(g => !hubs.Contains(g)).ToArray()) { _transit.UnregisterStop(grid); _addedStops.Remove(grid); }
        foreach (var (user, context) in _contexts.ToArray())
            if (!_players.TryGetSessionById(user, out var session)) _contexts.Remove(user);
            else if (context.Expires <= _timing.RealTime || session.AttachedEntity != context.Body || !CanUse(session, context.Terminal)) Close(session);
        foreach (var trip in _trips.Values.ToArray())
            if (!_apartments.Enabled || trip.Expires <= _timing.RealTime || Deleted(trip.Body) ||
                !_players.TryGetSessionById(trip.User, out var session) || session.Status != SessionStatus.InGame || session.AttachedEntity != trip.Body) CancelTrip(trip.User);
        foreach (var user in _returnStops.Keys.ToArray()) if (!_apartments.TryGetVisit(user, out _, out _)) _returnStops.Remove(user);
    }

    private bool HasElevator(EntityUid grid)
    {
        var query = EntityQueryEnumerator<ApartmentElevatorComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var transform))
            if (transform.GridUid == grid && transform.Anchored) return true;
        return false;
    }

    private void Cleanup()
    {
        foreach (var user in _trips.Keys.ToArray()) CancelTrip(user);
        foreach (var session in _players.Sessions) Close(session);
        foreach (var grid in _addedStops) _transit.UnregisterStop(grid);
        _addedStops.Clear(); _returnStops.Clear();
        _protection.SetStops(new HashSet<EntityUid>());
    }
}
