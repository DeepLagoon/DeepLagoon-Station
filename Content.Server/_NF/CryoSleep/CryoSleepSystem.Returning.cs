using Content.Server.Administration.Logs;
using Content.Shared.Bed.Sleep;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.CCVar;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Players;
using Robust.Shared.Network;

namespace Content.Server._NF.CryoSleep;

public sealed partial class CryoSleepSystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;

    private void InitReturning()
    {
        SubscribeNetworkEvent<WakeupRequestMessage>(OnWakeupMessage);
        SubscribeNetworkEvent<GetStatusMessage>(OnGetStatusMessage);
        SubscribeNetworkEvent<CryoCharacterListRequest>(OnCharacterListRequest);
        SubscribeNetworkEvent<CryoCharacterWakeupRequest>(OnCharacterWakeupRequest);
    }

    private void OnCharacterListRequest(
    CryoCharacterListRequest message,
    EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var response = new CryoCharacterListResponse();

        if (_gameTicker.CanWakeFromCryoLobby(session)
            && _configurationManager.GetCVar(
                NFCCVars.CryoReturnEnabled)
            && _storedBodies.TryGetValue(
                session.UserId, out var bodies))
        {
            foreach (var body in bodies)
            {
                if (TerminatingOrDeleted(body.Body))
                    continue;

                response.Characters.Add(new CryoCharacterEntry
                {
                    Id = body.Id,
                    Name = body.Name
                });
            }
        }

        RaiseNetworkEvent(response, session);
    }

    private void OnCharacterWakeupRequest(
        CryoCharacterWakeupRequest message,
        EntitySessionEventArgs args)
    {
        var session = args.SenderSession;

        if (!_gameTicker.CanWakeFromCryoLobby(session))
            return;

        if (!_storedBodies.TryGetValue(
            session.UserId, out var bodies))
            return;

        var index = bodies.FindIndex(
            body => body.Id == message.Id);

        if (index < 0)
            return;

        var result = TryWakeStoredBody(
            session.UserId, bodies, bodies[index]);

        if (result == ReturnToBodyStatus.Success)
            _gameTicker.PlayerJoinGame(session, silent: true);

        RaiseNetworkEvent(
            new WakeupRequestMessage.Response(result),
            session);
    }

    private void OnWakeupMessage(WakeupRequestMessage message, EntitySessionEventArgs session)
    {
        var entity = session.SenderSession.GetMind();

        var result = entity == null || !TryComp<MindComponent>(entity, out var mind)
            ? ReturnToBodyStatus.NotAGhost
            : TryReturnToBody(mind);

        var msg = new WakeupRequestMessage.Response(result);
        RaiseNetworkEvent(msg, session.SenderSession);
    }

    public void OnGetStatusMessage(GetStatusMessage message, EntitySessionEventArgs args)
    {
        var msg = new GetStatusMessage.Response(HasCryosleepingBody(args.SenderSession.UserId));
        RaiseNetworkEvent(msg, args.SenderSession);
    }

    private ReturnToBodyStatus TryWakeStoredBody(
    NetUserId id,
    List<StoredBody> bodies,
    StoredBody storedBody)
    {
        if (!_configurationManager.GetCVar(
            NFCCVars.CryoReturnEnabled))
            return ReturnToBodyStatus.Disabled;

        var body = storedBody.Body;
        var cryopod = storedBody.Cryopod;

        if (TerminatingOrDeleted(body)
            || Transform(body).MapUid != _storageMap)
            return ReturnToBodyStatus.BodyMissing;

        if (!TryComp<MindComponent>(
            storedBody.Mind, out var savedMind))
            return ReturnToBodyStatus.BodyMissing;

        if (savedMind.UserId != null
            && savedMind.UserId != id)
            return ReturnToBodyStatus.BodyMissing;

        if (savedMind.CurrentEntity is { } current
            && !HasComp<GhostComponent>(current))
            return ReturnToBodyStatus.BodyMissing;

        if (TerminatingOrDeleted(cryopod)
            || !TryComp<CryoSleepComponent>(
                cryopod, out var cryoComp)
            || MetaData(cryopod).EntityPaused
            || HasComp<ApartmentGridComponent>(
                Transform(cryopod).GridUid))
        {
            var query = EntityQueryEnumerator<
                CryoSleepFallbackComponent,
                CryoSleepComponent>();

            var found = false;

            while (query.MoveNext(
                out cryopod, out _, out cryoComp))
            {
                if (!HasComp<ApartmentGridComponent>(
                        Transform(cryopod).GridUid)
                    && !IsOccupied(cryoComp)
                    && _container.Insert(
                        body, cryoComp.BodyContainer))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return ReturnToBodyStatus.NoCryopodAvailable;
        }
        else
        {
            if (IsOccupied(cryoComp!)
                || !_container.Insert(
                    body, cryoComp!.BodyContainer))
                return ReturnToBodyStatus.Occupied;
        }

        if (_mind.TryGetMind(id, out var oldMindId,
                out var oldMind)
            && oldMindId != storedBody.Mind)
        {
            _mind.WipeMind(oldMindId, oldMind);
        }

        _mind.SetUserId(storedBody.Mind, id, savedMind);
        _mind.ControlMob(id, body);

        bodies.Remove(storedBody);

        if (bodies.Count == 0)
            _storedBodies.Remove(id);

        var sleep = EnsureComp<SleepingComponent>(body);
        sleep.CooldownEnd = TimeSpan.FromSeconds(5);

        _popup.PopupEntity(
            Loc.GetString("cryopod-wake-up",
                ("entity", body)), body);

        RaiseLocalEvent(body,
            new CryosleepWakeUpEvent(cryopod, id), true);

        _adminLogger.Add(
            LogType.LateJoin,
            LogImpact.Medium,
            $"{id} returned from cryosleep as {storedBody.Name}");

        return ReturnToBodyStatus.Success;
    }

    /// <summary>
    ///   Returns the mind to the original body, if any. The mind must be possessing a ghost, unless [force] is true.
    /// </summary>
    public ReturnToBodyStatus TryReturnToBody(
    MindComponent mind, bool force = false)
    {
        if (!_configurationManager.GetCVar(
            NFCCVars.CryoReturnEnabled))
            return ReturnToBodyStatus.Disabled;

        var id = mind.UserId;

        if (id == null
            || !_storedBodies.TryGetValue(
                id.Value, out var bodies)
            || bodies.Count == 0)
            return ReturnToBodyStatus.BodyMissing;

        if (!force &&
            (mind.CurrentEntity is not { Valid: true } ghost
            || !HasComp<GhostComponent>(ghost)))
            return ReturnToBodyStatus.NotAGhost;

        return TryWakeStoredBody(
            id.Value, bodies, bodies[^1]);
    }

    public bool HasCryosleepingBody(NetUserId id)
    {
        return _storedBodies.TryGetValue(id, out var bodies) && bodies.Count > 0;
    }
}
