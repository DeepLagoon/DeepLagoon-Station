using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.Apartments;

[AdminCommand(AdminFlags.Host)]
public sealed class ApartmentCommand : IConsoleCommand
{
    public string Command => "apartment";
    public string Description => "Локальный прототип квартир (HOST).";
    public string Help => "apartment enter [шаблон] | edit | leave | visit <хозяин> | status";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player || args.Length == 0) { shell.WriteLine(Help); return; }
        var system = IoCManager.Resolve<IEntityManager>().System<ApartmentSystem>();
        var players = IoCManager.Resolve<IPlayerManager>();
        string message;
        switch (args[0])
        {
            case "enter": system.Enter(player, player.UserId, out message, args.Length > 1 ? args[1] : null); break;
            case "leave": system.Leave(player.UserId, out message); break;
            case "edit": message = system.OpenEditor(player) ? "Редактор открыт." : "Сначала войдите в свою квартиру."; break;
            case "visit" when args.Length == 2:
                var owner = players.Sessions.FirstOrDefault(p => p.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (owner != null) system.Enter(player, owner.UserId, out message); else message = "Хозяин не найден.";
                break;
            case "status": message = $"Квартир: {system.Instances.Count}; заморожено: {system.Instances.Values.Count(i => i.Frozen)}."; break;
            default: message = Help; break;
        }
        shell.WriteLine(message);
    }
}
