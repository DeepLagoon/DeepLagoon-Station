using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Mapping;

/// <summary>Content map-loading boundary that records exact source files without changing the engine API.</summary>
public static class GridMapSourceExtensions
{
    public static bool TryLoadGridTracked(this MapLoaderSystem loader, MapId mapId, ResPath file,
        [NotNullWhen(true)] out Entity<MapGridComponent>? grid, DeserializationOptions? options = null,
        Vector2 offset = default, Angle rot = default)
    {
        if (!loader.TryLoadGrid(mapId, file, out grid, options, offset, rot)) return false;
        Record(grid.Value.Owner, file);
        return true;
    }

    public static bool TryLoadMapTracked(this MapLoaderSystem loader, ResPath file,
        [NotNullWhen(true)] out Entity<MapComponent>? map,
        [NotNullWhen(true)] out HashSet<Entity<MapGridComponent>>? grids, DeserializationOptions? options = null,
        Vector2 offset = default, Angle rot = default)
    {
        if (!loader.TryLoadMap(file, out map, out grids, options, offset, rot)) return false;
        foreach (var grid in grids) Record(grid.Owner, file);
        return true;
    }

    public static bool TryLoadGenericTracked(this MapLoaderSystem loader, ResPath file,
        [NotNullWhen(true)] out LoadResult? result, MapLoadOptions? options = null)
    {
        if (!loader.TryLoadGeneric(file, out result, options)) return false;
        foreach (var grid in result.Grids) Record(grid.Owner, file);
        return true;
    }

    private static void Record(EntityUid grid, ResPath file)
    {
        var path = file.ToRootedPath().Clean();
        var resources = IoCManager.Resolve<IResourceManager>();
        var provenance = IoCManager.Resolve<IEntityManager>().EnsureComponent<GridMapSourceComponent>(grid);
        provenance.Source = path.ToString();
        provenance.FromContent = !resources.UserData.Exists(path) && resources.ContentFileExists(path);
    }
}
