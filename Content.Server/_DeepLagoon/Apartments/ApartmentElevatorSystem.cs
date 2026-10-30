using System.Linq;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.Doors.Systems;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentElevatorReservedComponent : Component;

public sealed partial class ApartmentElevatorSystem : EntitySystem
{
    private const float TransferSmokeLead = .75f;
    [Dependency] private ApartmentSystem _apartments = default!;
    [Dependency] private ApartmentTravelSystem _travel = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private DoorSystem _doors = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private PopupSystem _popups = default!;

    private sealed record Context(Guid Token, EntityUid Panel, EntityUid Body, TimeSpan Expires);
    private sealed class Trip
    {
        public required EntityUid Source, Destination, SourceGrid, DestinationGrid;
        public required NetUserId Owner;
        public required bool Returning;
        public required List<(ICommonSession Session, EntityUid Body, Vector2 Offset)> Passengers;
        public required List<EntityUid> Doors;
        public TimeSpan Next;
        public TimeSpan ArrivesAt;
        public readonly List<EntityUid> Audio = new();
        public int Stage;
    }
    private readonly Dictionary<NetUserId, Context> _contexts = new();
    private readonly List<Trip> _trips = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentElevatorComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeNetworkEvent<ApartmentElevatorActionEvent>(OnAction);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => Cleanup());
    }

    private bool Inside(EntityUid body, EntityUid panel)
    {
        if (Deleted(body) || Deleted(panel) || !TryComp<ApartmentElevatorComponent>(panel, out var cabin)) return false;
        var transform = Transform(body);
        var origin = Transform(panel);
        var delta = (-origin.LocalRotation).RotateVec(transform.LocalPosition - origin.LocalPosition);
        return origin.Anchored && origin.GridUid != null && transform.ParentUid == origin.GridUid &&
            transform.GridUid == origin.GridUid && delta.X >= cabin.CabinMin.X - .45f && delta.X <= cabin.CabinMax.X + .45f &&
            delta.Y >= cabin.CabinMin.Y - .45f && delta.Y <= cabin.CabinMax.Y + .45f;
    }

    private bool CanUse(ICommonSession session, EntityUid panel) => _apartments.Enabled &&
        session.Status == SessionStatus.InGame && session.AttachedEntity is { } body && Inside(body, panel) &&
        TryComp<MobStateComponent>(body, out var mob) && mob.CurrentState == MobState.Alive && _interaction.InRangeUnobstructed(body, panel);

    private void OnActivate(EntityUid uid, ApartmentElevatorComponent comp, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex || !TryComp<ActorComponent>(args.User, out var actor) || !CanUse(actor.PlayerSession, uid)) return;
        args.Handled = true;
        var session = actor.PlayerSession;
        _contexts[session.UserId] = new Context(Guid.NewGuid(), uid, args.User, _timing.RealTime + TimeSpan.FromMinutes(2));
        Publish(session);
    }

    private bool Busy(EntityUid panel) => _trips.Any(t => t.Source == panel || t.Destination == panel);

    private void Publish(ICommonSession session, string message = "")
    {
        if (!_contexts.TryGetValue(session.UserId, out var context)) return;
        RaiseNetworkEvent(new ApartmentElevatorStateEvent(context.Token, _apartments.Number(session.UserId),
            HasComp<ApartmentGridComponent>(Transform(context.Panel).GridUid), Busy(context.Panel), message), session);
    }

    private void OnAction(ApartmentElevatorActionEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_contexts.TryGetValue(session.UserId, out var context) || context.Token != ev.Token) return;
        if (ev.Action == "close") { _contexts.Remove(session.UserId); return; }
        if (context.Expires <= _timing.RealTime || session.AttachedEntity != context.Body || !CanUse(session, context.Panel)) return;
        if (ev.Action is not ("depart" or "return")) return;
        if (Busy(context.Panel)) { Publish(session, "Лифт уже выполняет поездку."); return; }
        // Rotate the capability before any expensive map loading; stale/replayed actions cannot allocate homes.
        _contexts[session.UserId] = context with { Token = Guid.NewGuid() };
        Publish(session, RequestRide(session, context.Panel, ev.Number, ev.Action == "return"));
    }

    private EntityUid? PanelOn(EntityUid grid)
    {
        EntityUid? found = null;
        var query = EntityQueryEnumerator<ApartmentElevatorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.GridUid != grid || !transform.Anchored) continue;
            if (found != null) return null;
            found = uid;
        }
        return found;
    }

    private List<EntityUid>? CabinDoors(EntityUid panel)
    {
        var cabin = Comp<ApartmentElevatorComponent>(panel);
        if (cabin.DoorOffsets.Count == 0) return null;
        var doors = new List<EntityUid>();
        foreach (var offset in cabin.DoorOffsets)
        {
            EntityUid? found = null;
            var query = EntityQueryEnumerator<DoorComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var door, out var transform))
                if (transform.GridUid == Transform(panel).GridUid && transform.Anchored &&
                    Vector2.DistanceSquared(transform.LocalPosition, Transform(panel).LocalPosition + Transform(panel).LocalRotation.RotateVec(offset)) < .1f)
                {
                    if (HasComp<ApartmentEntranceComponent>(uid) || door.State == DoorState.Welded) return null;
                    found = uid;
                    break;
                }
            if (found == null) return null;
            doors.Add(found.Value);
        }
        return doors;
    }

    public string RequestRide(ICommonSession driver, EntityUid source, int number, bool returning)
    {
        if (Busy(source)) return "Лифт уже выполняет поездку.";
        if (!CanUse(driver, source)) return "Встаньте рядом с пультом внутри кабины.";
        var sourceGrid = Transform(source).GridUid!.Value;
        if (returning != HasComp<ApartmentGridComponent>(sourceGrid)) return "Выберите доступное направление.";
        NetUserId owner = driver.UserId;
        EntityUid? destination = null;
        if (returning)
        {
            var stops = EntityQueryEnumerator<ApartmentTransitStopComponent, TransformComponent>();
            while (stops.MoveNext(out var marker, out _, out var stop))
                if (_travel.ValidStop(marker) && stop.GridUid is { } grid && PanelOn(grid) is { } panel) { destination = panel; break; }
        }
        else
        {
            if (!HasComp<ApartmentProtectedGridComponent>(sourceGrid) || _apartments.NumberOwner(number) is not { } target)
                return "Неизвестный номер квартиры или пульт вне жилой остановки.";
            owner = target;
            try { destination = PanelOn(_apartments.PrepareElevatorApartment(owner).Grid); }
            catch (Exception e) { Log.Warning($"Elevator apartment load failed: {e}"); return "Не удалось загрузить квартиру."; }
        }
        if (destination == null || destination == source || Busy(destination.Value)) return "Лифт назначения недоступен. Проверьте шаблон квартиры.";
        var targetPanel = destination.Value;
        var destinationGrid = Transform(targetPanel).GridUid!.Value;
        var sourceCabin = Comp<ApartmentElevatorComponent>(source);
        var targetCabin = Comp<ApartmentElevatorComponent>(targetPanel);
        if (sourceCabin.CabinMin != targetCabin.CabinMin || sourceCabin.CabinMax != targetCabin.CabinMax)
            return "Размеры кабин не совпадают.";
        var sourceDoors = CabinDoors(source);
        var destinationDoors = CabinDoors(targetPanel);
        if (sourceDoors == null || destinationDoors == null) return "Не найдены двери лифта. Проверьте настройки пульта.";
        var passengers = new List<(ICommonSession, EntityUid, Vector2)>();
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } body || !Inside(body, source)) continue;
            if (session.Status != SessionStatus.InGame || !TryComp<MobStateComponent>(body, out var mob) || mob.CurrentState != MobState.Alive ||
                (TryComp<BuckleComponent>(body, out var buckle) && buckle.Buckled) || HasComp<ApartmentTripPendingComponent>(body))
                return "Все пассажиры должны быть живы, стоять в кабине и завершить предыдущую поездку.";
            if (returning ? !_apartments.TryGetVisit(session.UserId, out _, out _) : _apartments.TryGetVisit(session.UserId, out _, out _))
                return "У пассажира уже есть незавершённая поездка.";
            passengers.Add((session, body, (-Transform(source).LocalRotation).RotateVec(Transform(body).LocalPosition - Transform(source).LocalPosition)));
        }
        if (passengers.Count == 0 || passengers.All(p => p.Item1 != driver)) return "Встаньте в кабину.";
        if (!returning && _apartments.Instances.TryGetValue(owner, out var home) &&
            _apartments.Visitors(home).Length + passengers.Count > home.Template.MaxVisitors) return "В квартире недостаточно мест для всех пассажиров.";
        // Keep an occupied arrival cabin free of a second party.
        if (_players.Sessions.Any(s => s.AttachedEntity is { } body && Inside(body, targetPanel))) return "Кабина назначения занята.";
        var trip = new Trip { Source = source, Destination = targetPanel, SourceGrid = sourceGrid, DestinationGrid = destinationGrid,
            Owner = owner, Returning = returning, Passengers = passengers, Doors = sourceDoors.Concat(destinationDoors).Distinct().ToList(),
            Next = _timing.CurTime + TimeSpan.FromSeconds(5) };
        _trips.Add(trip);
        EnsureComp<ApartmentElevatorReservedComponent>(sourceGrid);
        EnsureComp<ApartmentElevatorReservedComponent>(destinationGrid);
        foreach (var door in trip.Doors) { EnsureComp<ApartmentElevatorDoorComponent>(door); _doors.TryClose(door); }
        foreach (var (_, body, _) in passengers) EnsureComp<ApartmentTripPendingComponent>(body);
        return "Двери закрываются. Оставайтесь в кабине.";
    }

    private bool Valid(Trip trip) => _apartments.Enabled && !Deleted(trip.Source) && !Deleted(trip.Destination) &&
        trip.Passengers.All(p => p.Session.Status == SessionStatus.InGame && p.Session.AttachedEntity == p.Body &&
            Inside(p.Body, trip.Source) && TryComp<MobStateComponent>(p.Body, out var mob) && mob.CurrentState == MobState.Alive &&
            (!TryComp<BuckleComponent>(p.Body, out var buckle) || !buckle.Buckled));

    private void Smoke(EntityUid panel)
    {
        var cabin = Comp<ApartmentElevatorComponent>(panel);
        for (var x = (int)cabin.CabinMin.X; x <= cabin.CabinMax.X; x++)
        for (var y = (int)cabin.CabinMin.Y; y <= cabin.CabinMax.Y; y++)
            Spawn("DLApartmentElevatorMist", new EntityCoordinates(Transform(panel).GridUid!.Value, Transform(panel).LocalPosition + Transform(panel).LocalRotation.RotateVec(new Vector2(x, y))));
        if (cabin.HissSound != null) _audio.PlayPvs(cabin.HissSound, panel);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var trip in _trips.ToArray())
        {
            if (Deleted(trip.Source) || Deleted(trip.Destination) || trip.Doors.Any(d => Deleted(d))) { Finish(trip, "Поездка отменена: лифт недоступен."); continue; }
            if (trip.Stage < 3 && !Valid(trip)) { Finish(trip, "Поездка отменена: пассажир покинул кабину."); continue; }
            var closed = trip.Doors.All(d => Comp<DoorComponent>(d).State == DoorState.Closed);
            if (trip.Stage == 0)
            {
                if (!closed) { if (_timing.CurTime >= trip.Next) Finish(trip, "Поездка отменена: двери не закрылись."); continue; }
                trip.Stage = 1;
                var cabin = Comp<ApartmentElevatorComponent>(trip.Source);
                var duration = TimeSpan.FromSeconds(Math.Clamp(cabin.TravelTime, 4, 30));
                if (cabin.TravelSound != null)
                {
                    var sound = _audio.ResolveSound(cabin.TravelSound);
                    duration = _audio.GetAudioLength(sound);
                    foreach (var (session, body, _) in trip.Passengers)
                        if (_audio.PlayEntity(sound, session, body, cabin.TravelSound.Params) is { } audio) trip.Audio.Add(audio.Entity);
                }
                trip.ArrivesAt = _timing.CurTime + duration;
                trip.Next = _timing.CurTime + duration / 2 - TimeSpan.FromSeconds(TransferSmokeLead);
            }
            else if (trip.Stage == 1 && _timing.CurTime >= trip.Next)
            {
                trip.Stage = 2;
                Smoke(trip.Source);
                Smoke(trip.Destination);
                trip.Next = _timing.CurTime + TimeSpan.FromSeconds(TransferSmokeLead);
            }
            else if (trip.Stage == 2 && _timing.CurTime >= trip.Next)
            {
                if (!closed) { Finish(trip, "Поездка отменена: двери открыты."); continue; }
                // All access/capacity and passenger checks have completed before touching escrow.
                var moved = new List<(ICommonSession Session, EntityUid Body, Vector2 Offset)>();
                var failed = false;
                foreach (var passenger in trip.Passengers)
                {
                    var (session, body, offset) = passenger;
                    var arrival = new EntityCoordinates(trip.DestinationGrid, Transform(trip.Destination).LocalPosition + Transform(trip.Destination).LocalRotation.RotateVec(offset));
                    bool success;
                    if (trip.Returning)
                    {
                        _apartments.SetReturnPoint(session.UserId, arrival);
                        success = _apartments.Leave(session.UserId, out _);
                    }
                    else
                    {
                        success = _apartments.Enter(session, trip.Owner, out _, returnPoint: new EntityCoordinates(trip.SourceGrid, Transform(trip.Source).LocalPosition + Transform(trip.Source).LocalRotation.RotateVec(offset)));
                        if (success) _transform.SetCoordinates(body, arrival);
                    }
                    if (!success) { failed = true; break; }
                    moved.Add(passenger);
                    if (_contexts.Remove(session.UserId)) RaiseNetworkEvent(new ApartmentElevatorClosedEvent(), session);
                }
                if (failed)
                {
                    // Entry failures restore original items and coordinates for the whole party.
                    if (!trip.Returning) foreach (var passenger in moved) _apartments.Leave(passenger.Session.UserId, out _);
                    Finish(trip, "Не удалось завершить поездку. Обратитесь к администратору, если часть группы уже прибыла.");
                    continue;
                }
                if (!trip.Returning)
                {
                    _chat.TrySendInGameICMessage(trip.Destination, "Ваша рабочая одежда и вещи были изъяты в целях безопасности. Они будут возвращены на обратном пути.", InGameICChatType.Speak, false, nameOverride: "Автоинформатор лифта", ignoreActionBlocker: true);
                    var sound = Comp<ApartmentElevatorComponent>(trip.Destination).AnnouncementSound;
                    if (sound != null) _audio.PlayPvs(sound, trip.Destination);
                }
                trip.Stage = 3;
                trip.Next = trip.ArrivesAt;
            }
            else if (trip.Stage == 3 && _timing.CurTime >= trip.Next)
            {
                if (Comp<ApartmentElevatorComponent>(trip.Destination).ArrivalSound is { } sound) _audio.PlayPvs(sound, trip.Destination);
                Finish(trip, "Лифт прибыл.");
            }
        }
        foreach (var (user, context) in _contexts.ToArray())
            if (!_players.TryGetSessionById(user, out var session) || session.AttachedEntity != context.Body ||
                context.Expires <= _timing.RealTime || !CanUse(session, context.Panel))
            {
                _contexts.Remove(user);
                if (session != null && session.Status == SessionStatus.InGame) RaiseNetworkEvent(new ApartmentElevatorClosedEvent(), session);
            }
    }

    private void Finish(Trip trip, string message)
    {
        _trips.Remove(trip);
        foreach (var audio in trip.Audio)
            if (!Deleted(audio)) _audio.Stop(audio);
        foreach (var door in trip.Doors)
            if (!Deleted(door)) { RemComp<ApartmentElevatorDoorComponent>(door); _doors.TryOpen(door); }
        foreach (var grid in new[] { trip.SourceGrid, trip.DestinationGrid })
            if (!Deleted(grid)) RemComp<ApartmentElevatorReservedComponent>(grid);
        foreach (var (session, body, _) in trip.Passengers)
        {
            if (!Deleted(body)) RemComp<ApartmentTripPendingComponent>(body);
            if (session.Status != SessionStatus.InGame) continue;
            _popups.PopupCursor(message, session);
            Publish(session, message);
        }
    }

    private void Cleanup()
    {
        foreach (var trip in _trips.ToArray()) Finish(trip, "Поездка отменена.");
        _contexts.Clear();
    }
}
