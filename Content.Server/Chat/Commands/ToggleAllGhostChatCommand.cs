using Content.Server.Administration;
using Content.Server.Chat.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Chat.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class ToggleAllGhostChatCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;

    public string Command => "chat:toggle_all";
    public string Description => Loc.GetString("cmd-chat-toggle-all-desc");
    public string Help => Command;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        if (shell.Player is not { } player || !_entities.System<ChatSystem>().TryToggleAdminGhostHearing(player, out var enabled))
        {
            shell.WriteError(Loc.GetString("cmd-chat-toggle-all-requires-admin-ghost"));
            return;
        }

        shell.WriteLine(Loc.GetString(enabled ? "cmd-chat-toggle-all-enabled" : "cmd-chat-toggle-all-disabled"));
    }
}
