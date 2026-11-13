using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public static class SectorWeather
{
    private const int Interval = 600;
    private const int Transition = 90;

    // Between transitions, reuse the same geometry and network state.
    public static long Sample(long elapsed)
    {
        elapsed = Math.Max(0, elapsed);
        return elapsed - Math.Max(0, elapsed % Interval - Transition);
    }

    public static SectorDefinition At(SectorDefinition baseline, long sample)
    {
        var period = sample / Interval;
        if (period == 0)
            return baseline;

        var t = (float) (sample % Interval) / Transition;
        t = t * t * (3 - 2 * t);
        return new SectorDefinition
        {
            Version = baseline.Version,
            Id = baseline.Id,
            Seed = baseline.Seed,
            Profile = baseline.Profile,
            Star = baseline.Star,
            Planets = baseline.Planets,
            Regions = baseline.Regions.Select(region => Drift(baseline.Seed, region, period, t)).ToList(),
        };
    }

    private static SpaceRegion Drift(uint sectorSeed, SpaceRegion region, long period, float t)
    {
        var seed = SectorRandom.Derive(sectorSeed ^ region.Seed, region.Id);
        var previous = Target(seed, period - 1);
        var next = Target(seed, period);
        var radius = region.Radius * MathHelper.Lerp(previous.Radius, next.Radius, t);
        var aspect = Math.Min(1, region.Aspect * MathHelper.Lerp(previous.Aspect, next.Aspect, t));
        var rotation = region.Rotation + MathHelper.Lerp(previous.Rotation, next.Rotation, t);
        // The named signal and its live relay stay put as the ellipse drifts around them.
        var signal = SpaceRegionGeometry.World(region, new Vector2(0.4f, 0));
        var center = signal - new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) * (radius * 0.4f);
        return new SpaceRegion
        {
            Id = region.Id, Kind = region.Kind, Seed = region.Seed, Intensity = region.Intensity,
            X = center.X, Y = center.Y, Radius = radius, Aspect = aspect, Rotation = rotation,
        };
    }

    private static (float Radius, float Aspect, float Rotation) Target(uint seed, long period)
    {
        if (period == 0)
            return (1, 1, 0);

        // Alternate larger and smaller targets to keep consecutive changes visible.
        var random = new SectorRandom(unchecked(seed + (uint) period * 0x9E3779B9u));
        var expanding = ((period + seed) & 1) == 0;
        var radius = expanding ? random.Range(1.04f, 1.10f) : random.Range(0.94f, 0.99f);
        return (radius, random.Range(0.90f, 1.07f), random.Range(-0.14f, 0.14f));
    }
}
