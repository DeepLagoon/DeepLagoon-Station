using Content.Shared.Materials;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mining;

/// <summary>Produces one selected raw material directly into a linked material silo.</summary>
[RegisterComponent]
public sealed partial class LaserDrillComponent : Component
{
    /// <summary>Material volume produced per cycle, using the original drill's stack sizes.</summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<MaterialPrototype>, int> Materials = new();

    [DataField]
    public ProtoId<MaterialPrototype>? SelectedMaterial;
}

[Serializable, NetSerializable]
public enum LaserDrillUiKey : byte { Key }

[Serializable, NetSerializable]
public enum LaserDrillStatus : byte { NoResource, NoSilo, SiloUnavailable, StorageFull, Disabled, Mining }

[Serializable, NetSerializable]
public sealed class LaserDrillState(
    Dictionary<ProtoId<MaterialPrototype>, int> materials,
    ProtoId<MaterialPrototype>? selectedMaterial,
    string siloName,
    LaserDrillStatus status) : BoundUserInterfaceState
{
    public readonly Dictionary<ProtoId<MaterialPrototype>, int> Materials = materials;
    public readonly ProtoId<MaterialPrototype>? SelectedMaterial = selectedMaterial;
    public readonly string SiloName = siloName;
    public readonly LaserDrillStatus Status = status;
}

[Serializable, NetSerializable]
public sealed class LaserDrillSelectMessage(ProtoId<MaterialPrototype> material) : BoundUserInterfaceMessage
{
    public readonly ProtoId<MaterialPrototype> Material = material;
}
