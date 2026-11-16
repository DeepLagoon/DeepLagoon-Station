using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.Ahelp;

[AdminCommand(AdminFlags.Adminhelp)]
public sealed class AhelpLogCommand : IConsoleCommand
{
    public string Command => "ahelp_log";
    public string Description => "Читает журнал тикета ahelp.";
    public string Help => "ahelp_log <номер> [до ID события]";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        long before = 0;
        if (args.Length is not (1 or 2) || !long.TryParse(args[0], out var number) || number <= 0 ||
            args.Length == 2 && (!long.TryParse(args[1], out before) || before <= 0))
        {
            shell.WriteError(Help);
            return;
        }
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<AhelpSystem>();
        if (!system.Enabled)
        {
            shell.WriteError("Тикеты ahelp отключены на этом слоте.");
            return;
        }
        try
        {
            var entries = await Task.Run(() => system.Store.History(number, before));
            foreach (var entry in entries)
            {
                var line = entry.Line;
                var at = DateTimeOffset.FromUnixTimeSeconds(line.At);
                var file = line.File == null ? "" : $" [файл {line.File}, ID {line.Id}]";
                shell.WriteLine($"{entry.Id} {at:O} {entry.Kind} {line.AuthorName}: {line.Text}{file}");
            }
            if (entries.Length == 0)
                shell.WriteLine("Записей нет.");
        }
        catch (Exception)
        {
            shell.WriteError("Журнал ahelp недоступен.");
        }
    }
}
