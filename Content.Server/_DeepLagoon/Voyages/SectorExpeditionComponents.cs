namespace Content.Server._DeepLagoon.Voyages;

/// <summary>Plain orbit map. Deliberately has no biome, sector environment or area generator.</summary>
[RegisterComponent]
public sealed partial class SectorExpeditionMapComponent : Component
{
    public string Sector = "";
    public Guid Cycle;
}
[RegisterComponent]
public sealed partial class SectorExpeditionShipComponent : Component
{
    public Guid Trip;
}
[RegisterComponent]
public sealed partial class SectorCrewIdentityComponent : Component
{
    [DataField] public Guid Identity;
}
public sealed class SectorPlanetGeneratedEvent(EntityUid ship, EntityUid map, ushort index) : EntityEventArgs
{
    public readonly EntityUid Ship = ship, Map = map;
    public readonly ushort Index = index;
}
public sealed class SectorPlanetFinishedEvent(EntityUid map, bool complete) : EntityEventArgs
{
    public readonly EntityUid Map = map;
    public readonly bool Complete = complete;
}
