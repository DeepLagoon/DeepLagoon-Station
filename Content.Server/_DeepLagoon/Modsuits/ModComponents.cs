using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModControlComponent : Component
{
    public const string CoreSlot = "core-container";

    [DataField]
    public int Capacity = 12;

    public Container Modules = default!;
    public EntityUid? DeployAction;
    public EntityUid? ActivateAction;
    public EntityUid? ModuleAction;
    public EntityUid? HardlightAction;
    public EntityUid? HardlightStyleAction;
    public EntityUid? SelectedModule;
    public ModRadialMode RadialMode;
    public ModDeployment? Deployment;

    [DataField]
    public ModHardlightParts HardlightParts;

    [DataField]
    public Color HardlightColor = Color.FromHex("#1AD1FF");

    [DataField]
    public ModHardlightEffect HardlightEffect;
}

[RegisterComponent]
public sealed partial class ModModuleComponent : Component
{
    [DataField] public bool Removable = true;
    [DataField] public bool AffectsWearer;
    [DataField] public float IdleCharge;
    [DataField] public float ActiveCharge;
    [DataField] public bool StartPinned;
    [DataField] public int StorageMaxItems;
    [DataField] public int StorageMaxWeight;
    [DataField]
    public int Complexity = 1;

    [DataField(required: true)]
    public string Family = default!;

    [DataField]
    public float UseCharge = 2;

    [DataField]
    public EntProtoId? Tool;

    [DataField]
    public EntProtoId? Dispense;

    [DataField]
    public ModModuleMode Mode;

    [DataField]
    public int TargetTemperature = 37;

    [DataField]
    public string? Slot;

    [DataField]
    public ComponentRegistry Effects = new();

    [DataField]
    public Dictionary<ProtoId<DamageTypePrototype>, float> DamageCoefficients = new();

    public EntityUid? Host;
    public EntityUid? PinnedAction;
    public EntityUid? DeployedTool;
    public EntityUid? EffectTarget;
    public readonly List<IComponent> AppliedEffects = new();
    public bool Active;
    [DataField] public TimeSpan UseDelay = TimeSpan.FromSeconds(1);
    public TimeSpan NextUse;
}

public enum ModModuleMode : byte
{
    Passive,
    Toggle,
    Tool,
    Storage,
    Dispense,
    Flashlight,
    Magboots,
    Jetpack,
    Cloak,
    ThermalRegulator,
    JumpJet,
}

[RegisterComponent]
public sealed partial class ModLinkedToolComponent : Component
{
    [DataField]
    public EntityUid Module;
}

[RegisterComponent]
public sealed partial class ModLinkComponent : Component
{
    [DataField]
    public string Slot = "back";

    [DataField]
    public int Frequency = 1459;

    [DataField]
    public float CallDraw = 0.5f;

    [DataField]
    public string Label = string.Empty;

    public string Number = string.Empty;
    public EntityUid? Action;
    public EntityUid? Wearer;
    public EntityUid? Peer;
    public EntityUid? Hologram;
    public TimeSpan RingUntil;
    public bool Incoming;
    public bool Connected;
    public ModUiState? LastState;
}

[RegisterComponent]
public sealed partial class ModLinkMultitoolComponent : Component
{
    public int Frequency;
}

[RegisterComponent]
public sealed partial class ModPoweredJetpackComponent : Component;
