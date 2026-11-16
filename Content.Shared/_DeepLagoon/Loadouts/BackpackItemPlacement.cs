using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Loadouts;

[Serializable, NetSerializable, DataDefinition]
public readonly partial record struct BackpackItemPlacement(
    [property: DataField] bool Removed,
    [property: DataField] int X,
    [property: DataField] int Y,
    [property: DataField] int Rotation,
    [property: DataField] string? Container = null);
