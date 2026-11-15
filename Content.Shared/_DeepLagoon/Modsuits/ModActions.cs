using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._DeepLagoon.Modsuits;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ModActionButtonComponent : Component
{
    public static readonly SpriteSpecifier Background = new SpriteSpecifier.Rsi(
        new ResPath("_DeepLagoon/Modsuits/actions.rsi"), "bg_tech_blue");

    [DataField, AutoNetworkedField]
    public bool Alternate;
}

public enum ModActionKind : byte
{
    Deploy,
    Activate,
    Module,
    Hardlight,
    HardlightStyle,
}

public sealed partial class ModSuitActionEvent : InstantActionEvent
{
    [DataField(required: true)]
    public ModActionKind Kind;
}

public sealed partial class ModPinnedModuleEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed partial class ModDeployStepEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public enum ModRadialKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record ModRadialChoice(int Id, string Name, SpriteSpecifier Icon, bool Selected);

[Serializable, NetSerializable]
public sealed class ModRadialState(ModRadialChoice[] choices) : BoundUserInterfaceState
{
    public readonly ModRadialChoice[] Choices = choices;
}

[Serializable, NetSerializable]
public sealed class ModRadialSelection(int choice) : BoundUserInterfaceMessage
{
    public readonly int Choice = choice;
}
