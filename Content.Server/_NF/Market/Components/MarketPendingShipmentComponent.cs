using Content.Server._NF.Market.Systems;

namespace Content.Server._NF.Market.Components;

[RegisterComponent, Access(typeof(MarketSystem))]
public sealed partial class MarketPendingShipmentComponent : Component
{
    [DataField]
    public List<MarketLot> Items = [];
}
