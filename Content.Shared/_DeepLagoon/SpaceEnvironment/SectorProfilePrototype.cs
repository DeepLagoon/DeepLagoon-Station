using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

[Prototype]
public sealed partial class SectorProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = "DeepLagoonFrontier";
    [DataField(required: true)] public string Name = "";
    [DataField] public int RegionCount = 36;
    [DataField] public float InnerRadius = 4000f;
    [DataField] public float OuterRadius = 28000f;
    [DataField] public float MinRegionRadius = 450f;
    [DataField] public float MaxRegionRadius = 1200f;
    [DataField] public int MinPlanets = 2;
    [DataField] public int MaxPlanets = 6;
    [DataField] public List<SpaceRegionKind> Kinds = new() { SpaceRegionKind.Dust };
    [DataField] public List<string> PlanetTemplates = new()
    {
        "PlanetTypeVolcanic", "PlanetTypeDesert", "PlanetTypeHabitable",
        "PlanetTypeIce", "PlanetTypeBarren", "PlanetTypeGasGiant", "PlanetTypeIceGiant",
    };
    [DataField] public List<string> Stars = new() { "TypeKStar", "TypeGStar", "TypeMStar" };
}
