#if DEBUG
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Voyages;

[AdminCommand(AdminFlags.Debug)]
public sealed partial class SectorPirateTestCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    public string Command => "sectorpiratetest";
    public string Description => "Создать Debug-стенд: боевой шаттл, заявка и один гарантированный пиратский корабль.";
    public string Help => "sectorpiratetest [имя игрока]";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var player = args.Length == 0 ? shell.Player : args.Length == 1 ?
            _players.Sessions.FirstOrDefault(s => s.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase) ||
                s.Name.EndsWith("@" + args[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (player?.AttachedEntity is not { } actor) { shell.WriteError("Нужен игрок, вошедший за персонажа. " + Help); return; }
        _ = _entities.System<SectorExpeditionSystem>().PreparePirateTest(player, actor);
    }
}
#endif
