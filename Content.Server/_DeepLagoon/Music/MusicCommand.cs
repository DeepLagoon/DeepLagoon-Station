using Content.Server._DeepLagoon.Music;
using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;

namespace Content.Server._DeepLagoon.Music;

[AdminCommand(AdminFlags.Round)]
public sealed class MusicCommand : IConsoleCommand
{
    public string Command => "music";
    public string Description => Loc.GetString("dl-music-command-desc");
    public string Help => Loc.GetString("dl-music-command-help");

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new MusicEui(), player);
    }
}
