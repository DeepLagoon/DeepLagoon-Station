using System.Globalization;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.Currency;

[AnyCommand]
public sealed partial class LagoonCoinPayCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "lc:pay";
    public string Description => Loc.GetString("lagoon-coin-pay-description");
    public string Help => Loc.GetString("lagoon-coin-pay-help");

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } sender)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var wallet = _entities.System<LagoonCoinSystem>();
        if (args.Length == 1)
            await wallet.ConfirmTransfer(sender, args[0]);
        else if (args.Length == 2 && long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0)
            wallet.PrepareTransfer(sender, args[0], amount);
        else
            wallet.ReplyToTransfer(sender, Help);
    }
}
