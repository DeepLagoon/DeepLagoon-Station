using Content.Server.Administration;
using Content.Server.NPC.HTN;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.NPC;

[AdminCommand(AdminFlags.Debug)]
public sealed partial class ReloadAiCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "reloadai";

    public string Description => Loc.GetString("cmd-reloadai-desc");

    public string Help => Loc.GetString("cmd-reloadai-help", ("command", Command));

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Loc.GetString("cmd-reloadai-wrong-args", ("command", Command)));
            return;
        }

        var htn = _entities.System<HTNSystem>();
        var (npcs, restored) = htn.ReloadAll();

        shell.WriteLine(Loc.GetString("cmd-reloadai-done", ("count", npcs), ("restored", restored)));
    }
}
