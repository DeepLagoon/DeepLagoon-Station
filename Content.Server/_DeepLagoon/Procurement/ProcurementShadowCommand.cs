using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.Procurement;

[AdminCommand(AdminFlags.Debug)]
public sealed class ProcurementShadowCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    public string Command => "procurementshadow";
    public string Description => "Show the observational procurement worker status; does not change prices or balances.";
    public string Help => "procurementshadow";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
        => shell.WriteLine(_entities.System<ProcurementSystem>().Status);
}
