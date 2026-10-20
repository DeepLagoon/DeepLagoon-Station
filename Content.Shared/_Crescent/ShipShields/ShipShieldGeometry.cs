using System.Numerics;

namespace Content.Shared._Crescent.ShipShields;

/// <summary>
/// Keeps the shield's oval opening and collision boundary outside the occupied grid tiles.
/// </summary>
public static class ShipShieldGeometry
{
    public const float InnerWidthRatio = 38f / 58f;
    public const float InnerHeightRatio = 26f / 44f;

    public static Vector2 GetRadii(Vector2 gridSize, float padding)
        => Vector2.Max((gridSize + new Vector2(padding)) * 0.5f, new Vector2(0.5f));

    /// <summary>
    /// Uniform expansion needed to put all four corners inside the oval opening.
    /// Call for occupied tiles rather than the grid AABB to avoid expanding around empty corners.
    /// </summary>
    public static float GetEnclosingScale(Box2 tileBounds, Vector2 center, Vector2 radii)
    {
        var extent = Vector2.Max(Vector2.Abs(tileBounds.BottomLeft - center),
            Vector2.Abs(tileBounds.TopRight - center));
        var innerRadii = radii * new Vector2(InnerWidthRatio, InnerHeightRatio);
        return MathF.Max(1f, (extent / innerRadii).Length());
    }
}
