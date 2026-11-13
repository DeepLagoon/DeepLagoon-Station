using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.ShipAssembly;

/// <summary>
/// A manufactured set of parts for installing a ship hardpoint or weapon.
/// </summary>
[RegisterComponent, Access(typeof(ShipAssemblySystem))]
public sealed partial class ShipAssemblyKitComponent : Component
{
    [DataField(required: true)]
    public EntProtoId Prototype;
}
