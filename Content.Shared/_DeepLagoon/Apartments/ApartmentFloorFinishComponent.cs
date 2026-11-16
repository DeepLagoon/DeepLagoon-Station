using Content.Shared.Maps;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentFloorFinishComponent : Component
{
    [DataField(required: true)]
    public ProtoId<ContentTileDefinition> Tile;
}
