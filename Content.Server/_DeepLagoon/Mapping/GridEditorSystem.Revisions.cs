using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Piping.Components;
using Content.Shared.Decals;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private readonly Dictionary<EntityUid, GridRevision> _gridRevisions = new();
    private readonly Dictionary<EntityUid, (GameTick Tick, List<GridEditorItem> Items)> _tileSnapshots = new();
    private readonly HashSet<EntityUid> _changedTiles = new();
    private static readonly TimeSpan SyncFallback = TimeSpan.FromSeconds(2);
    private sealed record EntityRevision(Vector2 Position, Angle Rotation, bool Anchored, string Prototype, Color Color);
    private sealed record GridRevision(GameTick Tiles, Dictionary<EntityUid, EntityRevision> Entities,
        Dictionary<Vector2i, (GameTick Tick, int Count)> Decals, TimeSpan Captured);

    private void TilesChanged(ref TileChangedEvent args)
    {
        if (!_tileSnapshots.Remove(args.Entity.Owner)) return;
        _changedTiles.Add(args.Entity.Owner);
    }

    private GridRevision ReadRevision(EntityUid uid, MapGridComponent grid)
    {
        var entities = new Dictionary<EntityUid, EntityRevision>();
        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!TryComp(child, out TransformComponent? transform) || !TryComp(child, out MetaDataComponent? meta) ||
                HasComp<ActorComponent>(child) || meta.EntityPrototype == null) continue;
            entities[child] = new EntityRevision(transform.LocalPosition, transform.LocalRotation, transform.Anchored,
                meta.EntityPrototype.ID, TryComp<AtmosPipeColorComponent>(child, out var color) ? color.Color : Color.White);
        }
        var decals = new Dictionary<Vector2i, (GameTick, int)>();
        if (TryComp<DecalGridComponent>(uid, out var component))
            foreach (var (key, chunk) in component.ChunkCollection.ChunkCollection)
                decals[key] = (chunk.LastModified, chunk.Decals.Count);
        return new GridRevision(grid.LastTileModifiedTick, entities, decals, _timing.RealTime);
    }

    private bool NeedsSynchronization(EntityUid uid)
    {
        if (!TryComp<MapGridComponent>(uid, out var grid)) return false;
        if (_changedTiles.Contains(uid) || !_gridRevisions.TryGetValue(uid, out var before) ||
            _timing.RealTime - before.Captured >= SyncFallback) return true;
        var after = ReadRevision(uid, grid);
        return before.Tiles != after.Tiles || before.Entities.Count != after.Entities.Count || before.Decals.Count != after.Decals.Count ||
            after.Entities.Any(pair => !before.Entities.TryGetValue(pair.Key, out var old) || old != pair.Value) ||
            after.Decals.Any(pair => !before.Decals.TryGetValue(pair.Key, out var old) || old != pair.Value);
    }

    private List<GridEditorItem> CaptureTiles(EntityUid uid, MapGridComponent grid)
    {
        if (_tileSnapshots.TryGetValue(uid, out var cached) && cached.Tick == grid.LastTileModifiedTick) return cached.Items;
        var items = new List<GridEditorItem>();
        foreach (var tile in _map.GetAllTiles(uid, grid))
            items.Add(new GridEditorItem { Id = $"t:{tile.GridIndices.X}:{tile.GridIndices.Y}", Kind = "tile",
                Prototype = _tiles[tile.Tile.TypeId].ID, X = tile.GridIndices.X, Y = tile.GridIndices.Y,
                Variant = tile.Tile.Variant, Flags = tile.Tile.Flags, Mirror = tile.Tile.RotationMirroring });
        _tileSnapshots[uid] = (grid.LastTileModifiedTick, items);
        return items;
    }

    private void PruneRevisions()
    {
        var grids = _drafts.Values.Select(draft => draft.Grid).ToHashSet();
        foreach (var uid in _tileSnapshots.Keys.Concat(_gridRevisions.Keys).Distinct().ToArray())
        {
            if (grids.Contains(uid)) continue;
            _tileSnapshots.Remove(uid); _gridRevisions.Remove(uid); _changedTiles.Remove(uid);
        }
    }
}
