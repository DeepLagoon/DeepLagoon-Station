#if DEVELOPMENT
using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.DiscordLink;

[AdminCommand(AdminFlags.Debug)]
public sealed class DiscordLinkPreviewCommand : IConsoleCommand
{
    public string Command => "discordlinkpreview";
    public string Description => "Открыть окно привязки Discord с временной базой в памяти.";
    public string Help => "discordlinkpreview [имя игрока]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var player = shell.Player;
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }
        if (args.Length == 1)
            IoCManager.Resolve<IPlayerManager>().TryGetSessionByUsername(args[0], out player);
        if (player == null)
        {
            shell.WriteError("Укажите подключённого игрока: " + Help);
            return;
        }

        // Preview codes are isolated from the real identity store and disappear on close.
        var store = new DiscordLinkStore(":memory:");
        var ui = new DiscordLinkEui(store, () => false, disposeStoreOnClose: true);
        IoCManager.Resolve<EuiManager>().OpenEui(ui, player);
        shell.WriteLine("Окно предпросмотра Discord открыто для " + player.Name);
    }
}
#endif
