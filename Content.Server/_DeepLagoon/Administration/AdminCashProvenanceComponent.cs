using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Administration;

/// <summary>Tracked units in a cash stack. Untagged units can share the stack.</summary>
[RegisterComponent]
public sealed partial class AdminCashProvenanceComponent : Component
{
    [DataField]
    public List<AdminCashPortion> Portions = new();
}

[DataDefinition]
public sealed partial class AdminCashPortion
{
    [DataField]
    public NetUserId Spawner;

    [DataField]
    public string Name = "";

    [DataField]
    public int Units;
}
