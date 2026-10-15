using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Robust.Shared.GameObjects;

// Retain the existing content call shape against the 288 map-query API.
public static class MapQueryCompat288
{
    public static List<Entity<MapGridComponent>> FindGridsIntersecting(this SharedMapSystem map, MapId id, Box2 bounds)
    {
        var grids = new List<Entity<MapGridComponent>>();
        map.FindGridsIntersecting(id, bounds, ref grids);
        return grids;
    }

    public static List<Entity<MapGridComponent>> FindGridsIntersecting(this SharedMapSystem map, MapId id, Box2Rotated bounds)
    {
        var grids = new List<Entity<MapGridComponent>>();
        map.FindGridsIntersecting(id, bounds, ref grids);
        return grids;
    }
}
