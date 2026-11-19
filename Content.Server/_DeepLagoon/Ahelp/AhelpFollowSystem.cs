using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Shared.Administration;
using Content.Shared.Follower;
using Content.Shared.Mind;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Server._DeepLagoon.Ahelp;

public sealed class AhelpFollowSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<NetUserId, (NetUserId Target, TimeSpan Until)> _pending = new();
    private TimeSpan _nextCheck;

    public void Cancel(NetUserId viewer) => _pending.Remove(viewer);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _pending.Clear());
    }

    public void Follow(NetUserId viewer, NetUserId target)
    {
        if (viewer == target || !_players.TryGetSessionById(viewer, out var session) ||
            _admins.GetAdminData(session)?.HasFlag(AdminFlags.Admin) != true)
            return;
        _pending[viewer] = (target, _timing.RealTime + TimeSpan.FromMinutes(2));
        if (!TryFollow(viewer, target))
            _chat.DispatchServerMessage(session, "Ожидание персонажа для следования. Следование начнётся, когда игрок покинет лобби и у вас будет игровое тело или призрак.");
        else
            _pending.Remove(viewer);
    }

    private bool TryFollow(NetUserId viewer, NetUserId target)
    {
        if (!_players.TryGetSessionById(viewer, out var admin) || admin.AttachedEntity is not { } observer)
            return false;
        EntityUid? entity = _players.TryGetSessionById(target, out var session) ? session.AttachedEntity : null;
        if (entity == null && EntityManager.System<SharedMindSystem>().TryGetMind(target, out _, out var mind))
            entity = mind.CurrentEntity;
        if (entity == null || Deleted(entity) || Terminating(entity.Value))
            return false;
        EntityManager.System<FollowerSystem>().StartFollowingEntity(observer, entity.Value);
        return true;
    }

    public override void Update(float frameTime)
    {
        if (_timing.RealTime < _nextCheck)
            return;
        _nextCheck = _timing.RealTime + TimeSpan.FromMilliseconds(500);
        foreach (var (viewer, request) in _pending.ToArray())
        {
            if (!_players.TryGetSessionById(viewer, out var admin) ||
                _admins.GetAdminData(admin)?.HasFlag(AdminFlags.Admin) != true ||
                TryFollow(viewer, request.Target))
                _pending.Remove(viewer);
            else if (_timing.RealTime >= request.Until)
            {
                _pending.Remove(viewer);
                _chat.DispatchServerMessage(admin, "Следование отменено: у игрока пока нет персонажа или призрака.");
            }
        }
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class AhelpFollowCommand : IConsoleCommand
{
    public string Command => "followuser";
    public string Description => "Следовать за игроком по ID, включая призраков вне видимости клиента.";
    public string Help => "followuser <user-id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } viewer || args.Length != 1 || !Guid.TryParse(args[0], out var target))
        {
            shell.WriteError(Help);
            return;
        }
        IoCManager.Resolve<IEntityManager>().System<AhelpFollowSystem>().Follow(viewer.UserId, new NetUserId(target));
    }
}
