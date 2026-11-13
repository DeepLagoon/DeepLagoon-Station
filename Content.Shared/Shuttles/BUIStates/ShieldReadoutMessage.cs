using Content.Shared.Exodus.ShipShields;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

[Serializable, NetSerializable]
public sealed class ShieldReadoutMessage : BoundUserInterfaceMessage
{
    public readonly ShipShieldState? Readout;

    public ShieldReadoutMessage(ShipShieldState? readout) => Readout = readout;
}
