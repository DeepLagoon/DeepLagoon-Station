using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
///     Purchase a Vessel from the console
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsolePurchaseMessage : BoundUserInterfaceMessage
{
    public int ExpectedPrice;
    public string Vessel; //vessel prototype ID

    public ShipyardConsolePurchaseMessage(string vessel, int expectedPrice = -1)
    {
        Vessel = vessel;
        ExpectedPrice = expectedPrice;
    }
}
