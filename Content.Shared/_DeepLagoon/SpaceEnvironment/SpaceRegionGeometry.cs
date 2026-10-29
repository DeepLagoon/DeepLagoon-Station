using System.Numerics;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

/// <summary>The radar boundary is the gameplay boundary. Visual particles stay inside this ellipse.</summary>
public static class SpaceRegionGeometry
{
    public const int ContourSegments = 32;

    public static Vector2 Local(SpaceRegion region, Vector2 position)
    {
        var delta = position - new Vector2(region.X, region.Y);
        var c = MathF.Cos(region.Rotation);
        var s = MathF.Sin(region.Rotation);
        return new Vector2(delta.X * c + delta.Y * s, -delta.X * s + delta.Y * c)
               / new Vector2(region.Radius, region.Radius * region.Aspect);
    }

    public static bool Contains(SpaceRegion region, Vector2 position) => Local(region, position).LengthSquared() <= 1f;

    public static bool Intersects(SpaceRegion region, Vector2 start, Vector2 end)
    {
        var a = Local(region, start);
        var delta = Local(region, end) - a;
        var length = delta.LengthSquared();
        var t = length > 0 ? Math.Clamp(-Vector2.Dot(a, delta) / length, 0f, 1f) : 0f;
        return (a + delta * t).LengthSquared() <= 1f;
    }

    public static Vector2 ContourPoint(SpaceRegion region, int index)
    {
        var angle = index * MathF.Tau / ContourSegments;
        var x = MathF.Cos(angle) * region.Radius;
        var y = MathF.Sin(angle) * region.Radius * region.Aspect;
        var c = MathF.Cos(region.Rotation);
        var s = MathF.Sin(region.Rotation);
        return new Vector2(region.X + x * c - y * s, region.Y + x * s + y * c);
    }

    public static Color Color(SpaceRegionKind kind) => kind switch
    {
        SpaceRegionKind.Radiation => new Color(0.5f, 1f, 0.3f),
        SpaceRegionKind.Plasma => new Color(0.6f, 0.6f, 1f),
        SpaceRegionKind.Debris => new Color(1f, 0.65f, 0.3f),
        SpaceRegionKind.Asteroids => new Color(0.75f, 0.65f, 0.5f),
        _ => new Color(0.5f, 0.85f, 1f),
    };
}
