#if DEBUG
using Content.Shared.Administration;
using Robust.Client;
using Robust.Shared.Console;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Voyages;

[AnyCommand]
public sealed class VoyagePrototypeCommand : IConsoleCommand
{
    public string Command => "voyageprototype";
    public string Description => "Open the local voyage-console interface prototype.";
    public string Help => "voyageprototype [wait]";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1 || args.Length == 1 && args[0] != "wait") { shell.WriteError(Help); return; }
        if (args.Length == 1) { Wait(shell, 0); return; }
        IoCManager.Resolve<IEntityManager>().System<VoyagePrototypeUiSystem>().Open();
    }
    private void Wait(IConsoleShell shell, int attempt)
    {
        if (IoCManager.Resolve<IBaseClient>().RunLevel == ClientRunLevel.InGame) Execute(shell, "", []);
        else if (attempt < 120) Timer.Spawn(500, () => Wait(shell, attempt + 1));
    }
}
#endif
