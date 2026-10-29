using System.Numerics;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

/// <summary>Immutable broad phase, built once per loaded sector; no queries over historical sectors.</summary>
public sealed class SpaceRegionIndex
{
    private const float CellSize = 2048f;
    private readonly Dictionary<Vector2i, List<SpaceRegion>> _cells = new();
    private readonly List<SpaceRegion> _regions;

    public SpaceRegionIndex(List<SpaceRegion> regions)
    {
        _regions = regions;
        foreach (var region in regions)
        {
            var min = Cell(new Vector2(region.X - region.Radius, region.Y - region.Radius));
            var max = Cell(new Vector2(region.X + region.Radius, region.Y + region.Radius));
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            {
                var cell = new Vector2i(x, y);
                if (!_cells.TryGetValue(cell, out var contents))
                    _cells.Add(cell, contents = new List<SpaceRegion>());
                contents.Add(region);
            }
        }
    }

    public void Query(Vector2 start, Vector2 end, HashSet<SpaceRegion> result)
    {
        result.Clear();
        var min = Cell(Vector2.Min(start, end));
        var max = Cell(Vector2.Max(start, end));
        // Teleports and enormous FTL displacements must not enumerate millions of empty cells.
        if ((long) (max.X - min.X + 1) * (max.Y - min.Y + 1) > 256)
        {
            foreach (var region in _regions) result.Add(region);
            return;
        }
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
            if (_cells.TryGetValue(new Vector2i(x, y), out var contents))
                foreach (var region in contents) result.Add(region);
    }

    private static Vector2i Cell(Vector2 position) => new((int) MathF.Floor(position.X / CellSize),
        (int) MathF.Floor(position.Y / CellSize));
}
