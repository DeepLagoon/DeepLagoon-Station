using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Currency;

[AdminCommand(AdminFlags.Host)]
public sealed partial class LagoonCoinGrantCommand : IConsoleCommand
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IEntityManager _entities = default!;
    public string Command => "lc:add";
    public string Description => "Выдать Lagoon Coin игроку. Требуется HOST; причина обязательна.";
    public string Help => "lc:add <имя онлайн-игрока или UUID> <количество> <причина>";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3 || !long.TryParse(args[1], out var amount) || amount <= 0)
        {
            shell.WriteError(Help);
            return;
        }
        var target = _players.Sessions.FirstOrDefault(p => p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (target == null && !Guid.TryParse(args[0], out _))
        {
            shell.WriteError("Игрок не найден. Для выдачи офлайн укажите UUID.");
            return;
        }
        var user = target?.UserId ?? new NetUserId(Guid.Parse(args[0]));
        var reason = string.Join(' ', args.Skip(2));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            shell.WriteError("Укажите причину длиной от 1 до 500 символов.");
            return;
        }
        try
        {
            var result = await _entities.System<LagoonCoinSystem>().Grant(user, amount, reason, shell.Player?.UserId);
            shell.WriteLine($"Выдано {amount} LC. Баланс: {result.Balance} LC.");
        }
        catch (Exception e) { shell.WriteError($"LC не выданы: {e.Message}"); }
    }
}
