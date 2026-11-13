using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public enum NavigationSiteKind : byte { Distress, DustSample, Archive, RadiationCache, Capacitor, Satellite }
public sealed record NavigationSite(string Id, NavigationSiteKind Kind, Vector2 Position, uint Seed, float Intensity);

/// <summary>Versioned, bounded descriptors. Appending regions never moves previously generated signals.</summary>
public static class NavigationSiteCatalog
{
    public static List<NavigationSite> Generate(SectorDefinition sector)
    {
        var sites = new List<NavigationSite>();
        foreach (var region in sector.Regions)
        {
            var kind = region.Kind switch
            {
                SpaceRegionKind.Dust => NavigationSiteKind.DustSample,
                SpaceRegionKind.Debris => NavigationSiteKind.Archive,
                SpaceRegionKind.Radiation => NavigationSiteKind.RadiationCache,
                SpaceRegionKind.Plasma => NavigationSiteKind.Capacitor,
                _ => (NavigationSiteKind?) null,
            };
            if (kind == null) continue;
            var seed = PlasmaWeather.Seed(sector.Seed, region.Id);
            // The dust sampler lies in the central corridor, the cache in its shielded pocket.
            var position = RegionPoint(region, new Vector2(0.4f, 0));
            sites.Add(new NavigationSite("v1/region/" + region.Id, kind.Value, position, seed, region.Intensity));
        }
        foreach (var planet in sector.Planets)
        {
            var seed = SectorRandom.Derive(sector.Seed, "sites-v1/planet/" + planet.Id);
            var angle = seed / (float) uint.MaxValue * MathF.Tau;
            sites.Add(new NavigationSite("v1/planet/" + planet.Id, NavigationSiteKind.Satellite,
                new Vector2(planet.X, planet.Y) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 1200f, seed, 0.25f));
        }
        // A sparse fixed chain in real open space, independent of the transient junk spawn sequence.
        var random = new SectorRandom(SectorRandom.Derive(sector.Seed, "sites-v1/distress"));
        for (var i = 0; i < 12; i++)
        {
            var position = new Vector2(random.Range(-24000f, 24000f), random.Range(-24000f, 24000f));
            sites.Add(new NavigationSite($"v1/distress/{i}", NavigationSiteKind.Distress, position, random.Next(), 0.1f));
        }
        return sites;
    }

    // Filter availability, never the random sequence: appended terrain must not move old beacon IDs.
    public static bool Available(SectorDefinition sector, NavigationSite site)
        => site.Kind != NavigationSiteKind.Distress
            || !sector.Regions.Exists(r => SpaceRegionGeometry.Contains(r, site.Position))
            && !sector.Planets.Exists(p => Vector2.DistanceSquared(site.Position, new Vector2(p.X, p.Y)) < 2000f * 2000f);

    public static Vector2 RegionPoint(SpaceRegion region, Vector2 local) => SpaceRegionGeometry.World(region, local);

    public static bool InDustCorridor(SpaceRegion region, Vector2 start, Vector2 end)
        => MathF.Abs(SpaceRegionGeometry.Local(region, start).Y) <= 0.12f
            && MathF.Abs(SpaceRegionGeometry.Local(region, end).Y) <= 0.12f;

    public static bool InRadiationPocket(SpaceRegion region, Vector2 position)
        => Vector2.DistanceSquared(SpaceRegionGeometry.Local(region, position), new Vector2(0.4f, 0)) <= 0.18f * 0.18f;
}
