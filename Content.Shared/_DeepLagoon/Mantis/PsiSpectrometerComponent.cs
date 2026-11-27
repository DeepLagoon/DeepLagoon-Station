using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mantis;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class PsiSpectrometerComponent : Component
{

    [DataField]
    public float Range = 8f;

    [DataField]
    public float HunterRangeMultiplier = 1.75f;

    [DataField]
    public float ScanDelay = 3f;

    [DataField]
    public string HunterFaction = "NoosphericHunter";

    [DataField]
    public bool RequiresPower;

    [DataField]
    public float ChargePerScan;

    [DataField, AutoNetworkedField]
    public bool HunterMode;

    [DataField, AutoNetworkedField]
    public bool MapOverlay;

    [ViewVariables]
    public EntityUid? HunterHolder;

    [ViewVariables]
    public TimeSpan NextScan;

    [ViewVariables]
    public int LastGlimmer;

    [ViewVariables, AutoNetworkedField]
    public List<NoosphericSignature> LastSignatures = new();
}

[Serializable, NetSerializable]
public enum PsiSpectrometerUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class PsiSpectrometerState(
    List<NoosphericSignature> signatures,
    int glimmer,
    bool hunterMode,
    bool ready,
    float range,
    bool mapOverlay) : BoundUserInterfaceState
{
    public readonly List<NoosphericSignature> Signatures = signatures;
    public readonly int Glimmer = glimmer;
    public readonly bool HunterMode = hunterMode;
    public readonly bool Ready = ready;
    public readonly float Range = range;
    public readonly bool MapOverlay = mapOverlay;
}

[Serializable, NetSerializable]
public sealed class PsiSpectrometerScanMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class PsiSpectrometerHunterMessage(bool enabled) : BoundUserInterfaceMessage
{
    public readonly bool Enabled = enabled;
}

[Serializable, NetSerializable]
public sealed class PsiSpectrometerMapMessage(bool enabled) : BoundUserInterfaceMessage
{
    public readonly bool Enabled = enabled;
}
