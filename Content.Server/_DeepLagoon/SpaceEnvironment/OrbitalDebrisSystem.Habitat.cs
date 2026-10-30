using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public enum SpaceJunkHabitatKind : byte { OpenSpace, PlanetOrbit, DebrisField, AsteroidField }
public readonly record struct SpaceJunkHabitat(SpaceJunkHabitatKind Kind, int Target, int MaxExtent, int TileBudget, int Interval, bool Rich);

public sealed partial class OrbitalDebrisSystem
{
    public const float ReactionSeconds = 8f;
    public static SpaceJunkHabitat Habitat(SectorDefinition sector, Vector2 position)
    {
        foreach (var region in sector.Regions)
        {
            if (region.Kind is not (SpaceRegionKind.Debris or SpaceRegionKind.Asteroids) || !SpaceRegionGeometry.Contains(region, position)) continue;
            return new SpaceJunkHabitat(region.Kind == SpaceRegionKind.Debris ? SpaceJunkHabitatKind.DebrisField : SpaceJunkHabitatKind.AsteroidField,
                10, 10, 36, 1, true);
        }
        foreach (var planet in sector.Planets)
            if (Vector2.DistanceSquared(position, new Vector2(planet.X, planet.Y)) <= 2000f * 2000f)
                return new SpaceJunkHabitat(SpaceJunkHabitatKind.PlanetOrbit, 3, 4, 16, 12, false);
        return new SpaceJunkHabitat(SpaceJunkHabitatKind.OpenSpace, 1, 4, 16, 90, false);
    }

    /// <summary>For moving ships, new junk starts at least eight seconds ahead of the view horizon.</summary>
    public static Vector2 ForecastSpawn(Observer observer, float lateral, float extraSeconds, float angle)
    {
        var speed = observer.Velocity.Length();
        if (speed > 4f)
        {
            var forward = observer.Velocity / speed;
            var side = new Vector2(-forward.Y, forward.X);
            return observer.Position + forward * (observer.Range + (speed + 8f) * (ReactionSeconds + extraSeconds) + 16f)
                + side * (lateral * observer.Range * 0.75f);
        }
        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var edge = (observer.Range - 16f) / MathF.Max(MathF.Abs(direction.X), MathF.Abs(direction.Y));
        return observer.Position + direction * (edge + 12f);
    }

    public readonly record struct EncounterPlan(Vector2 Position, Vector2 Anchor, Vector2 Target,
        float Speed, float Direction, float ArrivalSeconds);

    public static float NormalViewHalf(Observer observer) => MathF.Max(4f, observer.Range - 32f);

    /// <summary>Backtrack the orbit from a future encounter, rather than hoping a random offscreen orbit becomes visible.</summary>
    public static EncounterPlan PlanEncounter(SectorDefinition sector, Observer observer, uint seed,
        float lateral, float extraSeconds, float angle, bool reverse = false)
    {
        var random = new SectorRandom(seed);
        var speed = random.Range(2f, 6f);
        var direction = random.Index(2) == 0 ? -1f : 1f;
        if (reverse) direction = -direction;
        var travel = observer.Velocity.Length();
        float arrival;
        Vector2 target;
        if (travel > 4f)
        {
            var forward = observer.Velocity / travel;
            var lead = Vector2.Dot(ForecastSpawn(observer, 0, extraSeconds, angle) - observer.Position, forward) + 16f;
            arrival = lead / travel;
            target = observer.Position + observer.Velocity * arrival
                + new Vector2(-forward.Y, forward.X) * (lateral * NormalViewHalf(observer) * 0.2f);
        }
        else
        {
            arrival = Math.Clamp((observer.Range + 16f) / speed + extraSeconds, 8f, 60f);
            target = observer.Position + observer.Velocity * arrival
                + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (NormalViewHalf(observer) * 0.2f);
        }
        // Tiny circles around a field center never cross a stationary observer's normal viewport.
        var anchor = ClosestEncounterAnchor(sector, target);
        var radius = Vector2.Distance(target, anchor);
        var start = anchor + RotateOrbit(target - anchor, -direction * speed / radius * arrival);
        return new EncounterPlan(start, anchor, target, speed, direction, arrival);
    }

    private static Vector2 ClosestEncounterAnchor(SectorDefinition sector, Vector2 target)
    {
        var nearest = Vector2.Zero;
        var best = float.PositiveInfinity;
        foreach (var planet in sector.Planets) Consider(new Vector2(planet.X, planet.Y));
        foreach (var region in sector.Regions) Consider(new Vector2(region.X, region.Y));
        return float.IsPositiveInfinity(best) ? ClosestAnchor(sector, target) + new Vector2(768f, 0) : nearest;
        void Consider(Vector2 candidate)
        {
            var squared = Vector2.DistanceSquared(candidate, target);
            if (squared < 256f * 256f || squared >= best) return;
            best = squared;
            nearest = candidate;
        }
    }

    public static Vector2 RotateOrbit(Vector2 offset, float angle)
    {
        var sin = MathF.Sin(angle);
        var cos = MathF.Cos(angle);
        return new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
    }

    public static List<Vector2i> JunkShape(int width, int height, int budget, ref SectorRandom random)
    {
        var cells = new List<Vector2i>();
        var seen = new HashSet<Vector2i>();
        void Add(int x, int y)
        {
            var cell = new Vector2i(x, y);
            if (x >= 0 && y >= 0 && x < width && y < height && cells.Count < budget && seen.Add(cell)) cells.Add(cell);
        }
        // A connected cross gives the intended footprint without filling a 10x10 square with entities.
        for (var x = 0; x < width; x++) Add(x, height / 2);
        for (var y = 0; y < height; y++) Add(width / 2, y);
        for (var attempt = 0; attempt < budget * 12 && cells.Count < Math.Min(budget, width * height); attempt++)
        {
            var from = cells[random.Index(cells.Count)];
            var direction = random.Index(4);
            Add(from.X + (direction == 0 ? 1 : direction == 1 ? -1 : 0), from.Y + (direction == 2 ? 1 : direction == 3 ? -1 : 0));
        }
        return cells;
    }

    private static string ScrapWall(ref SectorRandom random, bool rich)
    {
        var roll = random.Index(100);
        if (!rich) return roll < 65 ? "WallRockScrapPile" : roll < 80 ? "WallRockScrapPileIron"
            : roll < 90 ? "WallRockScrapPileQuartz" : roll < 97 ? "WallRockScrapPileCoal" : "WallRockScrapPileSilver";
        return roll < 45 ? "WallRockScrapPile" : roll < 65 ? "WallRockScrapPileIron" : roll < 75 ? "WallRockScrapPileCoal"
            : roll < 85 ? "WallRockScrapPileQuartz" : roll < 90 ? "WallRockScrapPileGold" : roll < 95 ? "WallRockScrapPileSilver"
            : roll < 98 ? "WallRockScrapPilePlasma" : roll == 98 ? "WallRockScrapPileUranium" : "WallRockScrapPileBluespace";
    }
}
