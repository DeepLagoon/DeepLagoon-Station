using System.Numerics;

namespace Content.Client._DeepLagoon.Voyages;

/// <summary>Soft overlapping sensor envelopes; visibility and hit-testing remain controlled by exploration.</summary>
public static class VoyageFogMask
{
    public static float Opacity(Vector2 point, ReadOnlySpan<Vector2> visible)
    {
        var distance = float.PositiveInfinity;
        foreach (var center in visible) distance = MathF.Min(distance, Vector2.DistanceSquared(point, center));
        var edge = Math.Clamp((MathF.Sqrt(distance) - 320f) / 320f, 0, 1);
        return edge * edge * (3 - 2 * edge);
    }
}
