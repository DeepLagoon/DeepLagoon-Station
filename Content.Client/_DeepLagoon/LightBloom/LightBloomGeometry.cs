using System.Numerics;

namespace Content.Client._DeepLagoon.LightBloom;

/// <summary>Small, convex visibility meshes. Blockers retain their actual polygon and render transform.</summary>
internal sealed class LightBloomGeometry
{
    public const float MaxRadius = 1.5f;
    private const int CircleSegments = 32;
    private const int MaxNearbyEdges = 128;
    private const float Margin = 0.0001f;
    private readonly List<(Vector2 A, Vector2 B)> _edges = new();
    private readonly List<(int Start, int Count)> _polygons = new();
    private readonly List<(Vector2 A, Vector2 B)> _nearby = new();
    private readonly List<float> _angles = new();

    public void Clear()
    {
        _edges.Clear();
        _polygons.Clear();
    }

    public void AddBlocker(ReadOnlySpan<Vector2> polygon, Matrix3x2 transform)
    {
        _polygons.Add((_edges.Count, polygon.Length));
        for (var i = 0; i < polygon.Length; i++)
            _edges.Add((Vector2.Transform(polygon[i], transform),
                Vector2.Transform(polygon[(i + 1) % polygon.Length], transform)));
    }

    public bool IsVisible(Vector2 eye, Vector2 source)
    {
        if (IsEmbedded(source))
            return false;
        var direction = source - eye;
        foreach (var (a, b) in _edges)
            if (RayHit(eye, direction, a, b, out var hit) && hit < 1f - Margin)
                return false;
        return true;
    }

    public int Build(Vector2 source, float radius, Span<Vector2> vertices)
    {
        if (IsEmbedded(source))
            return 0;
        _nearby.Clear();
        _angles.Clear();
        for (var i = 0; i < CircleSegments; i++)
            _angles.Add(i * MathF.Tau / CircleSegments);

        foreach (var (a, b) in _edges)
        {
            if (DistanceToSegmentSquared(source, a, b) > radius * radius)
                continue;
            // Omit an over-budget halo instead of dropping walls from its visibility calculation.
            if (_nearby.Count == MaxNearbyEdges)
                return 0;
            _nearby.Add((a, b));
            AddAngles(a - source);
            AddAngles(b - source);
        }

        _angles.Sort();
        var count = _angles.Count + 2;
        if (vertices.Length < count)
            return 0;
        vertices[0] = source;
        for (var i = 0; i < _angles.Count; i++)
        {
            var direction = new Vector2(MathF.Cos(_angles[i]), MathF.Sin(_angles[i]));
            var distance = radius;
            foreach (var (a, b) in _nearby)
                if (RayHit(source, direction, a, b, out var hit))
                    distance = Math.Min(distance, Math.Max(0, hit - Margin));
            vertices[i + 1] = source + direction * distance;
        }
        vertices[count - 1] = vertices[1];
        return count;
    }

    public const int MaxVertices = CircleSegments + MaxNearbyEdges * 6 + 2;

    private bool IsEmbedded(Vector2 point)
    {
        foreach (var (start, count) in _polygons)
        {
            var positive = true;
            var negative = true;
            for (var i = start; i < start + count; i++)
            {
                var (a, b) = _edges[i];
                var side = Cross(b - a, point - a);
                positive &= side > 0.000001f;
                negative &= side < -0.000001f;
            }
            if (count >= 3 && (positive || negative))
                return true;
        }
        return false;
    }

    private void AddAngles(Vector2 point)
    {
        var angle = MathF.Atan2(point.Y, point.X);
        _angles.Add(Normalize(angle - Margin));
        _angles.Add(Normalize(angle));
        _angles.Add(Normalize(angle + Margin));
    }

    private static float Normalize(float angle) => (angle % MathF.Tau + MathF.Tau) % MathF.Tau;

    internal static bool RayHit(Vector2 origin, Vector2 direction, Vector2 a, Vector2 b, out float distance)
    {
        var edge = b - a;
        var denominator = Cross(direction, edge);
        distance = 0;
        if (MathF.Abs(denominator) < 0.0000001f)
            return false;
        var delta = a - origin;
        distance = Cross(delta, edge) / denominator;
        var along = Cross(delta, direction) / denominator;
        return distance >= 0 && along >= -0.00001f && along <= 1.00001f;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static float DistanceToSegmentSquared(Vector2 point, Vector2 a, Vector2 b)
    {
        var edge = b - a;
        var t = edge.LengthSquared() > 0
            ? Math.Clamp(Vector2.Dot(point - a, edge) / edge.LengthSquared(), 0, 1)
            : 0;
        return Vector2.DistanceSquared(point, a + t * edge);
    }
}
