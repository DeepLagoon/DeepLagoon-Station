using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server.Players.RateLimiting;
using Content.Server._Goobstation.Administration;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Mind;
using Content.Shared.Players.RateLimiting;
using Content.Shared._DeepLagoon.Ahelp;
using Robust.Server.Player;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed class AhelpPlayerActionsSystem : EntitySystem
{
    private const string Rate = "AhelpPlayerActions";
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EuiManager _euis = default!;
    [Dependency] private PlayerRateLimitManager _rate = default!;

    public override void Initialize()
    {
        _rate.Register(Rate, new RateLimitRegistration(CCVars.AhelpRateLimitPeriod, CCVars.AhelpRateLimitCount, null));
        SubscribeNetworkEvent<AhelpPlayerActionRequest>(OnRequest);
    }

    private void OnRequest(AhelpPlayerActionRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var flag = request.Action switch
        {
            "vv" => AdminFlags.VarEdit,
            "time" => AdminFlags.Moderator,
            "logs" => AdminFlags.Logs,
            _ => AdminFlags.None,
        };
        if (request.Player == Guid.Empty || flag == AdminFlags.None ||
            !_admins.HasAdminFlag(session, AdminFlags.Adminhelp) || !_admins.HasAdminFlag(session, flag) ||
            _rate.CountAction(session, Rate) != RateLimitStatus.Allowed)
            return;
        switch (request.Action)
        {
            case "logs":
                _euis.OpenEui(new AdminLogsEui { InitialPlayer = request.Player }, session);
                break;
            case "time":
                _euis.OpenEui(new TimeTransferPanelEui { InitialPlayer = request.Player.ToString() }, session);
                break;
            case "vv":
                var id = new NetUserId(request.Player);
                var entity = _players.TryGetSessionById(id, out var player) ? player.AttachedEntity : null;
                if (entity == null && EntityManager.System<SharedMindSystem>().TryGetMind(id, out _, out var mind))
                    entity = mind.VisitingEntity ?? mind.OwnedEntity;
                var valid = entity is { } uid && !TerminatingOrDeleted(uid);
                RaiseNetworkEvent(new AhelpPlayerActionResult(request.Player,
                    valid ? GetNetEntity(entity!.Value) : null, valid ? "" : "У игрока нет тела или призрака для VV."), session.Channel);
                break;
        }
    }
}
