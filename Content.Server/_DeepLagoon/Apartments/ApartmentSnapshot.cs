using Content.Shared._DeepLagoon.Apartments;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>The native grid save and the editor identities that accompany it. Never sent to clients.</summary>
public sealed class ApartmentSnapshot
{
    public string Grid { get; set; } = "";
    public ApartmentDelta Layout { get; set; } = new();
    public List<ApartmentMapFurniture> MapFurniture { get; set; } = new();
}

[RegisterComponent]
public sealed partial class ApartmentFurnitureIdentityComponent : Component
{
    [DataField] public string Id = "";
    [DataField] public string Furniture = "";
}

/// <summary>Contents are discarded at the round boundary, not when the apartment is unloaded.</summary>
[RegisterComponent]
public sealed partial class ApartmentTrashComponent : Component;
