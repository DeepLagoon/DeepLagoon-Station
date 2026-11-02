namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Temporary equipment bound to the apartment that issued it.</summary>
[RegisterComponent]
public sealed partial class ApartmentChameleonGearComponent : Component
{
    public EntityUid Grid;
    public Robust.Shared.Map.EntityCoordinates ReturnCoordinates;
}
