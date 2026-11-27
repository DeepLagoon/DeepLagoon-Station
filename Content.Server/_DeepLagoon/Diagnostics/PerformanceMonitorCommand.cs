using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.Diagnostics;

[AdminCommand(AdminFlags.Debug)]
public sealed partial class PerformanceMonitorCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entities = default!;
    public override string Command => "perfmonitor";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0 || shell.Player is not { } player || !_entities.System<PerformanceMonitorSystem>().Open(player))
            shell.WriteError(Loc.GetString("cmd-perfmonitor-client-required"));
    }
}
