using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._DeepLagoon.BodyCustomization;

[AnyCommand]
public sealed class SetGenderArousalCommand : IConsoleCommand
{
    public string Command => "set_gender_arousal";
    public string Description => "Переключить состояние полового органа своего персонажа.";
    public string Help => "set_gender_arousal [0|1] — переключить состояние или задать его явно.";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        bool? aroused = null;
        if (args.Length > 1 || args.Length == 1 && args[0] is not ("0" or "1"))
        { shell.WriteError(Help); return; }
        if (args.Length == 1) aroused = args[0] == "1";
        if (shell.Player?.AttachedEntity is not { } uid)
        { shell.WriteError("Сначала войдите в своего персонажа."); return; }
        var entities = IoCManager.Resolve<IEntityManager>();
        if (!entities.System<BodyCustomizationSystem>().SetGenitalArousal(uid, aroused))
        { shell.WriteError("У персонажа нет соответствующей части тела."); return; }
        shell.WriteLine("Состояние изменено.");
    }
}
