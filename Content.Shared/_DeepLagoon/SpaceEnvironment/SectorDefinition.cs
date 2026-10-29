using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

/// <summary>Persist the manifest, not just its seed: generator upgrades must not rewrite visited sectors.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class SectorDefinition
{
    public const int CurrentVersion = 1;
    [DataField] public int Version = CurrentVersion;
    [DataField] public string Id = "";
    [DataField] public uint Seed;
    [DataField] public string Profile = "DeepLagoonFrontier";
    [DataField] public string Star = "StarKyphrus";
    [DataField] public List<SectorPlanet> Planets = new();
    [DataField] public List<SpaceRegion> Regions = new();
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class SectorPlanet
{
    [DataField] public string Id = "";
    [DataField] public string Template = "PlanetTypeHabitable";
    [DataField] public string Name = "";
    [DataField] public float X;
    [DataField] public float Y;
    [DataField] public uint AppearanceSeed;
}

[Serializable, NetSerializable]
public enum SpaceRegionKind : byte
{
    Dust,
    Asteroids,
    Debris,
    Plasma,
    Radiation,
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class SpaceRegion
{
    [DataField] public string Id = "";
    [DataField] public SpaceRegionKind Kind;
    [DataField] public float X;
    [DataField] public float Y;
    [DataField] public float Radius;
    [DataField] public float Aspect = 1f;
    [DataField] public float Rotation;
    [DataField] public float Intensity = 1f;
    [DataField] public uint Seed;
}
