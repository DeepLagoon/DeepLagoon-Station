using Content.Shared._FarHorizons.StarSystem.Helpers;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

public static class PlanetAppearanceGenerator
{
    public static void Apply(Planet planet, uint seed)
    {
        var random = new SectorRandom(seed);
        planet.Rotation = random.Range(0, MathF.Tau);
        planet.HueShift += random.Range(-0.65f, 0.65f);
        planet.SaturationShift += random.Range(-0.25f, 0.25f);
        // A prototype is shared by every planet. Each appearance must own its shader values.
        planet.CustomData = new PlanetCustomValues
        {
            Floats = new Dictionary<string, float>(planet.CustomData.Floats),
            Ints = new Dictionary<string, int>(planet.CustomData.Ints),
            Colors = new Dictionary<string, Color>(planet.CustomData.Colors),
        };
        foreach (var key in new[] { "terrainScale", "macroFrequency", "ridgeFrequency", "detailFrequency",
                     "bandFrequency", "turbulenceScale", "cloudScale", "stormFrequency" })
        {
            if (planet.CustomData.Floats.TryGetValue(key, out var value))
                planet.CustomData.Floats[key] = value * random.Range(0.7f, 1.35f);
        }
        if (planet.Atmosphere is { } atmosphere)
        {
            atmosphere.CloudCoverage *= random.Range(0.45f, 1f);
            atmosphere.CloudScale *= random.Range(0.7f, 1.4f);
        }
    }
}
