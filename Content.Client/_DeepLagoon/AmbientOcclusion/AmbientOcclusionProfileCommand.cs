using System.Globalization;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

[AnyCommand]
public sealed class AmbientOcclusionProfileCommand : IConsoleCommand
{
    public string Command => "ao_profile";
    public string Description => "Compare AO off, walls, texture mask, sprite silhouettes and legacy renderer.";
    public string Help => "ao_profile [seconds-per-mode, 3..30] | ao_profile stop. Default: 5s per mode plus 2s warm-up; 35s total.";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<AmbientOcclusionSystem>();
        if (args.Length == 1 && args[0] == "stop")
        {
            system.StopProfile();
            shell.WriteLine("AO profile stopped. Normal AO settings restored.");
            return;
        }
        var seconds = 5;
        if (args.Length > 1 || (args.Length == 1 && (!int.TryParse(args[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out seconds) || seconds < 3 || seconds > 30)))
        {
            shell.WriteError(Help);
            return;
        }
        system.StartProfile(line => shell.WriteLine(line), seconds);
        shell.WriteLine($"AO profile started: Off → Walls → Mask → Silhouette → Legacy; {5 * (seconds + 2)}s total. Settings are temporarily overridden, not saved. Legacy can reproduce the FPS drop. Use ao_profile stop to cancel.");
    }
}
