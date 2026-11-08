using Content.Server.Administration.Logs;
using Content.Shared.Bed.Sleep;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.CCVar;
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

    /// <summary>
    ///   Returns the mind to the original body, if any. The mind must be possessing a ghost, unless [force] is true.
    /// </summary>
    public ReturnToBodyStatus TryReturnToBody(MindComponent mind, bool force = false)
    {
        if (!_configurationManager.GetCVar(NFCCVars.CryoReturnEnabled))
            return ReturnToBodyStatus.Disabled;

        var id = mind.UserId;
        if (id == null || !_storedBodies.TryGetValue(id.Value, out var bodies) || !bodies.TryPeek(out var storedBody))
            return ReturnToBodyStatus.BodyMissing;

        if (!force && (mind.CurrentEntity is not { Valid: true } ghost || !HasComp<GhostComponent>(ghost)))
            return ReturnToBodyStatus.NotAGhost;

        var cryopod = storedBody.Cryopod;
        var body = storedBody.Body;
        if (Deleted(body))
            return ReturnToBodyStatus.BodyMissing;

        if (TerminatingOrDeleted(cryopod) || !TryComp<CryoSleepComponent>(cryopod, out var cryoComp) ||
            MetaData(cryopod).EntityPaused)
        {
            var fallbackQuery = EntityQueryEnumerator<CryoSleepFallbackComponent, CryoSleepComponent>();
            bool foundFallback = false;
            while (fallbackQuery.MoveNext(out cryopod, out _, out cryoComp))
            {
                if (!IsOccupied(cryoComp) && _container.Insert(body, cryoComp.BodyContainer))
                {
                    foundFallback = true;
                    break;
                }
            }

            // No valid cryopod, all fallbacks occupied or missing.
            if (!foundFallback)
                return ReturnToBodyStatus.NoCryopodAvailable;
        }
        else
        {
            // If the original pod is active but occupied, wait for it to become available.
            if (IsOccupied(cryoComp!) || !_container.Insert(body, cryoComp!.BodyContainer))
                return ReturnToBodyStatus.Occupied;
        }

        bodies.Pop();
        if (bodies.Count == 0)
            _storedBodies.Remove(id.Value);
        _mind.ControlMob(id.Value, body);
        // Force the mob to sleep
        var sleep = EnsureComp<SleepingComponent>(body);
        sleep.CooldownEnd = TimeSpan.FromSeconds(5);

        _popup.PopupEntity(Loc.GetString("cryopod-wake-up", ("entity", body)), body);

        RaiseLocalEvent(body, new CryosleepWakeUpEvent(cryopod, id), true);

        _adminLogger.Add(LogType.LateJoin, LogImpact.Medium, $"{id.Value} has returned from cryosleep!");
        return ReturnToBodyStatus.Success;
    }

    public bool HasCryosleepingBody(NetUserId id)
    {
        return _storedBodies.TryGetValue(id, out var bodies) && bodies.Count > 0;
    }
}
