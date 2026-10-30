using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Physical goods, delivered once to the apartment, separate from furniture packs.</summary>
[Prototype]
public sealed partial class ApartmentItemPrototype : IPrototype
{
    [IdDataField] public string ID { get; set; } = default!;
    [DataField(required: true)] public EntProtoId Entity;
    [DataField] public long Price;
    [DataField] public string Category = "Декор";
}
