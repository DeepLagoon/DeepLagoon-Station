using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.Art;

[AdminCommand(AdminFlags.None)]
public sealed class ArtModerationCommand : IConsoleCommand
{
    public string Command => "artmoderation";
    public string Description => Loc.GetString("dl-art-moderation-title");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player || !IoCManager.Resolve<IAdminManager>().HasAdminFlag(player, AdminFlags.None)) return;
        IoCManager.Resolve<EuiManager>().OpenEui(new ArtModerationEui(), player);
    }
}
