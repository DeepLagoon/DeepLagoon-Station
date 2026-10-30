using System.Numerics;
using Content.Shared._FarHorizons.StarSystem.Prototypes;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

public static class SectorGenerator
{
    public const int MaxRegions = 160;
    public const int MaxPlanets = 8;

    public static SectorDefinition Generate(string id, uint seed, SectorProfilePrototype profile)
    {
        ValidateProfile(profile);
        var sector = new SectorDefinition { Id = id, Seed = seed, Profile = profile.ID };
        var random = new SectorRandom(SectorRandom.Derive(seed, "bodies"));
        sector.Star = profile.Stars[random.Index(profile.Stars.Count)];
        var count = profile.MinPlanets + random.Index(profile.MaxPlanets - profile.MinPlanets + 1);
        for (var i = 0; i < count; i++)
        {
            var bodyId = $"planet-{i}";
            var angle = random.Range(0, MathF.Tau);
            var distance = 6500f + i * 3000f + random.Range(0, 900f);
            sector.Planets.Add(new SectorPlanet
            {
                Id = bodyId,
                Name = $"{id} / {i + 1}",
                Template = profile.PlanetTemplates[random.Index(profile.PlanetTemplates.Count)],
                X = MathF.Cos(angle) * distance,
                Y = MathF.Sin(angle) * distance,
                AppearanceSeed = SectorRandom.Derive(seed, bodyId),
            });
        }
        GenerateRegions(sector, profile);
        return sector;
    }

    public static SectorDefinition FromSystem(string id, uint seed, string systemId,
        StarSystemPrototype system, SectorProfilePrototype profile)
    {
        ValidateProfile(profile);
        var sector = new SectorDefinition { Id = id, Seed = seed, Profile = profile.ID, Star = system.Star.Id };
        for (var i = 0; i < system.Planets.Count; i++)
        {
            var entry = system.Planets[i];
            sector.Planets.Add(new SectorPlanet
            {
                Id = entry.Planet.Id,
                Template = entry.Planet.Id,
                X = MathF.Cos(entry.Angle) * entry.Distance,
                Y = MathF.Sin(entry.Angle) * entry.Distance,
                AppearanceSeed = SectorRandom.Derive(seed, systemId + "/" + entry.Planet.Id),
            });
        }
        GenerateRegions(sector, profile);
        return sector;
    }

    /// <summary>Append-only density upgrade. Existing positions, identities and appearance seeds remain intact.</summary>
    public static SectorDefinition ExpandRegions(SectorDefinition original, SectorProfilePrototype profile)
    {
        ValidateProfile(profile);
        var expanded = new SectorDefinition
        {
            Version = original.Version, Id = original.Id, Seed = original.Seed, Profile = original.Profile,
            Star = original.Star, Planets = new(original.Planets), Regions = new(original.Regions),
        };
        GenerateRegions(expanded, profile, "region-expansion-v1", "extra-region-");
        return expanded;
    }

    private static void GenerateRegions(SectorDefinition sector, SectorProfilePrototype profile,
        string stream = "regions", string prefix = "region-")
    {
        var random = new SectorRandom(SectorRandom.Derive(sector.Seed, stream));
        var nextId = sector.Regions.Count;
        for (var attempt = 0; attempt < profile.RegionCount * 24 && sector.Regions.Count < profile.RegionCount; attempt++)
        {
            var angle = random.Range(0, MathF.Tau);
            var distance = MathF.Sqrt(random.Range(profile.InnerRadius * profile.InnerRadius,
                profile.OuterRadius * profile.OuterRadius));
            var center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
            var radius = random.Range(profile.MinRegionRadius, profile.MaxRegionRadius);
            var clear = true;
            foreach (var region in sector.Regions)
                clear &= Vector2.Distance(center, new Vector2(region.X, region.Y)) > radius + region.Radius + 200f;
            foreach (var planet in sector.Planets)
                clear &= Vector2.Distance(center, new Vector2(planet.X, planet.Y)) > radius + 800f;
            if (!clear)
                continue;
            var id = $"{prefix}{nextId++}";
            while (sector.Regions.Exists(region => region.Id == id)) id = $"{prefix}{nextId++}";
            sector.Regions.Add(new SpaceRegion
            {
                Id = id, X = center.X, Y = center.Y, Radius = radius,
                Kind = profile.Kinds[random.Index(profile.Kinds.Count)],
                Aspect = random.Range(0.5f, 1f), Rotation = random.Range(0, MathF.Tau),
                Intensity = random.Range(0.4f, 1f), Seed = SectorRandom.Derive(sector.Seed, id),
            });
        }
    }

    private static void ValidateProfile(SectorProfilePrototype profile)
    {
        if (profile.RegionCount is < 0 or > MaxRegions || profile.MinPlanets < 0
            || profile.MaxPlanets < profile.MinPlanets || profile.MaxPlanets > MaxPlanets
            || profile.Kinds.Count == 0 || profile.Stars.Count == 0 || profile.PlanetTemplates.Count == 0
            || !float.IsFinite(profile.InnerRadius) || !float.IsFinite(profile.OuterRadius)
            || profile.InnerRadius < 3000f || profile.OuterRadius > 100000f || profile.OuterRadius <= profile.InnerRadius
            || !float.IsFinite(profile.MinRegionRadius) || !float.IsFinite(profile.MaxRegionRadius)
            || profile.MinRegionRadius < 100f || profile.MaxRegionRadius < profile.MinRegionRadius
            || profile.MaxRegionRadius > 2000f)
            throw new ArgumentException($"Invalid sector profile: {profile.ID}");
    }
}
