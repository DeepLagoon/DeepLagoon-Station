using Content.Shared._NF.Market;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Market;

/// <summary>
/// Server-side stock for a prototype and resale limit. The client sees the available lot for each prototype.
/// </summary>
[DataDefinition]
public sealed partial record MarketLot
{
    [DataField]
    public EntProtoId Prototype { get; set; }

    [DataField]
    public ProtoId<StackPrototype>? StackPrototype { get; set; }

    [DataField]
    public int Quantity { get; set; }

    [DataField]
    public double Price { get; set; }

    [DataField]
    public double? CraftedUnitLimit { get; set; }

    public MarketData ToMarketData() => new(Prototype, StackPrototype, Quantity, Price);
}
