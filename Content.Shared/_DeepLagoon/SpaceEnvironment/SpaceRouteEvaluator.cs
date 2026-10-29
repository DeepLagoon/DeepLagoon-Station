using System.Numerics;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

/// <summary>A bounded route estimate for future UI/autopilot. FTL evaluates endpoints, never a physical corridor.</summary>
public static class SpaceRouteEvaluator
{
    public readonly record struct Estimate(float Distance, float DustDistance, float RadiationDistance,
        bool PlasmaAtDeparture, bool PlasmaAtArrival);

    public static Estimate Evaluate(Vector2 start, Vector2 end, IReadOnlyList<SpaceRegion> regions)
    {
        var distance = Vector2.Distance(start, end);
        var dust = 0f;
        var radiation = 0f;
        var departure = false;
        var arrival = false;
        foreach (var region in regions)
        {
            if (region.Kind == SpaceRegionKind.Plasma)
            {
                departure |= SpaceRegionGeometry.Contains(region, start);
                arrival |= SpaceRegionGeometry.Contains(region, end);
            }
            var a = SpaceRegionGeometry.Local(region, start);
            var delta = SpaceRegionGeometry.Local(region, end) - a;
            var aa = delta.LengthSquared();
            if (aa < 0.000001f) continue;
            var b = 2f * Vector2.Dot(a, delta);
            var c = a.LengthSquared() - 1f;
            var discriminant = b * b - 4f * aa * c;
            if (discriminant < 0) continue;
            var root = MathF.Sqrt(discriminant);
            var enter = Math.Clamp((-b - root) / (2f * aa), 0, 1);
            var exit = Math.Clamp((-b + root) / (2f * aa), 0, 1);
            var traversed = MathF.Max(0, exit - enter) * distance * region.Intensity;
            if (region.Kind == SpaceRegionKind.Radiation) radiation += traversed;
            else if (region.Kind != SpaceRegionKind.Plasma) dust += traversed;
        }
        return new Estimate(distance, dust, radiation, departure, arrival);
    }
}
