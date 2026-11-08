// Mono - Refactored into smaller subsystems
using System.Diagnostics;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Robust.Shared.Collections;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using Robust.Shared.Map.Enumerators;

namespace Content.Server.Parallax;

public sealed partial class BiomeSystem
{
    private readonly record struct MarkerRequest(BiomeComponent Biome, string Layer, Vector2i Origin);
    private readonly Dictionary<MarkerRequest, bool> _pendingMarkerPreloads = new();

    private void QueueMarkerPreloads()
    {
        foreach (var (biome, layers) in _markerChunks)
        {
            foreach (var (layer, origins) in layers)
            {
                foreach (var origin in origins)
                {
                    var request = new MarkerRequest(biome, layer, origin);
                    var forced = biome.ForcedMarkerLayers.Contains(layer);
                    if (forced || !_pendingMarkerPreloads.ContainsKey(request))
                        _pendingMarkerPreloads[request] = forced;
                }
            }

            biome.ForcedMarkerLayers.Clear();
        }

        _markerChunks.Clear();
    }

    private const int MarkerTilesPerTick = 2048;
    private static readonly TimeSpan MarkerScanTime = TimeSpan.FromMilliseconds(1);
    private MarkerScan? _markerScan;

    private sealed class MarkerCandidates(HashSet<Vector2i> tiles)
    {
        public readonly HashSet<Vector2i> Tiles = tiles;
        public readonly Dictionary<Vector2i, EntityUid> Entities = new();
        public readonly Dictionary<Vector2i, string?> Masks = new();
    }

    private sealed class MarkerScan(MarkerRequest request, BiomeMarkerLayerPrototype layer,
        Box2i bounds, int count, int seed, bool forced, MarkerCandidates candidates)
    {
        public readonly MarkerRequest Request = request;
        public readonly BiomeMarkerLayerPrototype Layer = layer;
        public readonly Box2i Bounds = bounds;
        public readonly int Count = count;
        public readonly int Seed = seed;
        public readonly bool Forced = forced;
        public readonly MarkerCandidates Candidates = candidates;
        public int NextTile;
    }

    private void ProcessMarkerScan()
    {
        if (_markerScan == null)
            StartMarkerPreload();

        if (_markerScan is not { } scan)
            return;

        var uid = scan.Request.Biome.Owner;
        var biome = scan.Request.Biome;
        if (!biome.Running || !biome.Enabled || TerminatingOrDeleted(uid) ||
            !TryComp<MapGridComponent>(uid, out var grid))
        {
            CancelMarkerScan(biome);
            return;
        }

        if (scan.NextTile < scan.Bounds.Area)
        {
            var end = Math.Min(scan.Bounds.Area, scan.NextTile + MarkerTilesPerTick);
            var start = Stopwatch.GetTimestamp();
            while (scan.NextTile < end)
            {
                var index = scan.NextTile++;
                var tile = new Vector2i(scan.Bounds.Left + index / scan.Bounds.Height,
                    scan.Bounds.Bottom + index % scan.Bounds.Height);
                ScanMarkerTile(uid, biome, grid, scan.Layer, scan.Forced, tile, scan.Candidates);
                if ((scan.NextTile & 31) == 0 && Stopwatch.GetElapsedTime(start) >= MarkerScanTime)
                    break;
            }
            return;
        }

        _markerScan = null;
        try
        {
            FinishMarkerScan(scan, uid, grid);
        }
        finally
        {
            _tilePool.Return(scan.Candidates.Tiles);
        }
    }

    private void StartMarkerPreload()
    {
        foreach (var (request, forced) in _pendingMarkerPreloads)
        {
            var (biome, layer, origin) = request;
            _pendingMarkerPreloads.Remove(request);
            if (!biome.Running || !biome.Enabled || TerminatingOrDeleted(biome.Owner))
                return;

            var layerIndex = 0;
            foreach (var id in biome.MarkerLayers)
            {
                layerIndex++;
                if (id != layer)
                    continue;

                if (!biome.LoadedMarkers.TryGetValue(layer, out var loaded) || !loaded.Contains(origin))
                    StartMarkerScan(request, ProtoManager.Index(id), layerIndex, forced);
                return;
            }

            return;
        }
    }

    private void CancelMarkerScan(BiomeComponent biome)
    {
        if (_markerScan is not { } scan || scan.Request.Biome != biome)
            return;

        _tilePool.Return(scan.Candidates.Tiles);
        _markerScan = null;
    }

    /// <summary>
    /// Prepare only the marker regions that can affect the next chunk.
    /// A new region scan runs over multiple ticks before the chunk is ready.
    /// </summary>
    private bool PrepareChunkMarkers(BiomeComponent biome, Vector2i chunk)
    {
        var layerIndex = 0;
        foreach (var id in biome.MarkerLayers)
        {
            layerIndex++;
            var layer = ProtoManager.Index(id);
            var area = new Box2(chunk, chunk + new Vector2i(ChunkSize - 1, ChunkSize - 1));
            var regions = new ChunkIndicesEnumerator(area, layer.Size);
            while (regions.MoveNext(out var index))
            {
                var origin = index.Value * layer.Size;
                if (biome.LoadedMarkers.TryGetValue(id, out var loaded) && loaded.Contains(origin))
                    continue;

                if (_markerScan == null)
                {
                    var request = new MarkerRequest(biome, id.Id, origin);
                    _pendingMarkerPreloads.Remove(request, out var forced);
                    StartMarkerScan(request, layer, layerIndex, forced);
                }
                return false;
            }
        }

        return true;
    }

    private void StartMarkerScan(MarkerRequest request, BiomeMarkerLayerPrototype layer, int layerIndex, bool forced)
    {
        var (biome, _, origin) = request;
        var seed = biome.Seed + origin.X * ChunkSize + origin.Y + layerIndex;
        var buffer = (int)(layer.Radius / 2f);
        var bounds = new Box2i(origin + buffer, origin + layer.Size - buffer);
        var count = Math.Min((int)(bounds.Area / (layer.Radius * layer.Radius)), layer.MaxCount);
        if (count <= 0)
        {
            biome.LoadedMarkers.GetOrNew(layer.ID).Add(origin);
            return;
        }

        _markerScan = new MarkerScan(request, layer, bounds, count, seed, forced, new MarkerCandidates(_tilePool.Get()));
    }

    private void FinishMarkerScan(MarkerScan scan, EntityUid uid, MapGridComponent grid)
    {
        var (biome, layer, origin) = scan.Request;
        SelectMarkerNodes(scan.Layer, scan.Count, new Random(scan.Seed), scan.Candidates, out var spawnSet, out var existing);

        foreach (var ent in existing)
        {
            if (!_xformQuery.TryGetComponent(ent, out var xform) || !xform.Anchored || xform.ParentUid != uid)
                continue;

            var tile = _mapSystem.LocalToTile(uid, grid, xform.Coordinates);
            var chunk = SharedMapSystem.GetChunkIndices(tile, ChunkSize) * ChunkSize;
            if (scan.Candidates.Entities.GetValueOrDefault(tile) != ent ||
                biome.ModifiedTiles.TryGetValue(chunk, out var modified) && modified.Contains(tile))
                continue;

            // Replacing generated terrain with a marker is part of generation, not mining.
            _generatedEntityTiles.Remove(ent);
            _scenerySpriteDefaults.Remove(ent);
            if (biome.LoadedEntities.TryGetValue(chunk, out var entities))
                entities.Remove(ent);
            Del(ent);
        }

        foreach (var node in spawnSet.Keys)
        {
            var chunk = SharedMapSystem.GetChunkIndices(node, ChunkSize) * ChunkSize;
            biome.PendingMarkers.GetOrNew(chunk).GetOrNew(layer).Add(node);
        }

        biome.LoadedMarkers.GetOrNew(layer).Add(origin);
    }

    /// <summary>
    /// Gets the marker nodes for the specified area.
    /// </summary>
    /// <param name="emptyTiles">Should we include empty tiles when determine markers (e.g. if they are yet to be loaded)</param>
    public void GetMarkerNodes(
        EntityUid gridUid,
        BiomeComponent biome,
        MapGridComponent grid,
        BiomeMarkerLayerPrototype layerProto,
        bool forced,
        Box2i bounds,
        int count,
        Random rand,
        out Dictionary<Vector2i, string?> spawnSet,
        out HashSet<EntityUid> existingEnts,
        bool emptyTiles = true)
    {
        var candidates = new MarkerCandidates(_tilePool.Get());
        try
        {
            // Collect all valid tiles before choosing any groups, so sparse masks still spawn their veins.
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                for (var y = bounds.Bottom; y < bounds.Top; y++)
                {
                    var tile = new Vector2i(x, y);
                    if (!emptyTiles && (!_mapSystem.TryGetTile(grid, tile, out var existing) || existing.IsEmpty))
                        continue;

                    ScanMarkerTile(gridUid, biome, grid, layerProto, forced, tile, candidates);
                }
            }

            SelectMarkerNodes(layerProto, count, rand, candidates, out spawnSet, out existingEnts);
        }
        finally
        {
            _tilePool.Return(candidates.Tiles);
        }
    }

    private void ScanMarkerTile(EntityUid uid, BiomeComponent biome, MapGridComponent grid, BiomeMarkerLayerPrototype layer,
        bool forced, Vector2i node, MarkerCandidates candidates)
    {
        var anchored = _mapSystem.GetAnchoredEntities(uid, grid, node);
        anchored.MoveNext(out var existing);
        if (!forced && existing != null)
            return;

        TryGetEntity(node, biome, (uid, grid), out var prototype);
        if (layer.EntityMask.Count > 0 && (prototype == null || !layer.EntityMask.ContainsKey(prototype)))
            return;

        if (prototype != null && layer.Prototype != null)
            return;

        candidates.Tiles.Add(node);
        candidates.Masks.Add(node, prototype);
        if (existing is { } entity)
            candidates.Entities.Add(node, entity);
    }

    private void SelectMarkerNodes(BiomeMarkerLayerPrototype layerProto, int count, Random rand,
        MarkerCandidates candidates, out Dictionary<Vector2i, string?> spawnSet, out HashSet<EntityUid> existingEnts)
    {
        var remainingTiles = candidates.Tiles;
        var frontier = new ValueList<Vector2i>(32);
        // TODO: Need poisson but crashes whenever I use moony's due to inputs or smth idk
        // Get the total amount of groups to spawn across the entire chunk.
        // We treat a null entity mask as requiring nothing else on the tile

        spawnSet = new Dictionary<Vector2i, string?>();
        existingEnts = new HashSet<EntityUid>();

        // Iterate the group counts and pathfind out each group.
        for (var i = 0; i < count; i++)
        {
            var groupSize = rand.Next(layerProto.MinGroupSize, layerProto.MaxGroupSize + 1);

            // While we have remaining tiles keep iterating
            while (groupSize > 0 && remainingTiles.Count > 0)
            {
                var startNode = rand.PickAndTake(remainingTiles);
                frontier.Clear();
                frontier.Add(startNode);

                // This essentially may lead to a vein being split in multiple areas but the count matters more than position.
                while (frontier.Count > 0 && groupSize > 0)
                {
                    // Need to pick a random index so we don't just get straight lines of ores.
                    var frontierIndex = rand.Next(frontier.Count);
                    var node = frontier[frontierIndex];
                    frontier.RemoveSwap(frontierIndex);
                    remainingTiles.Remove(node);

                    // Add neighbors if they're valid, worst case we add no more and pick another random seed tile.
                    for (var x = -1; x <= 1; x++)
                    {
                        for (var y = -1; y <= 1; y++)
                        {
                            var neighbor = new Vector2i(node.X + x, node.Y + y);

                            if (frontier.Contains(neighbor) || !remainingTiles.Contains(neighbor))
                                continue;

                            frontier.Add(neighbor);
                        }
                    }

                    // Tile valid salad so add it.
                    var mask = candidates.Masks[node];
                    spawnSet.Add(node, mask);
                    groupSize--;

                    if (candidates.Entities.TryGetValue(node, out var existing))
                        existingEnts.Add(existing);
                }
            }

            if (groupSize > 0)
            {
                Log.Warning($"Found remaining group size for ore veins!");
            }
        }
    }

    /// <summary>
    /// Loads the pre-deteremined marker nodes for a particular chunk.
    /// This is calculated by the marker region scan.
    /// </summary>
    /// <remarks>
    /// Note that the marker chunks do not correspond to this chunk.
    /// </remarks>
    private void LoadChunkMarkers(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed)
    {
        // Load any pending marker tiles first.
        if (!component.PendingMarkers.TryGetValue(chunk, out var layers))
            return;

        // This needs to be done separately in case we try to add a marker layer and want to force it on existing
        // loaded chunks.
        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= _tilePool.Get();

        foreach (var (layer, nodes) in layers)
        {
            var layerProto = ProtoManager.Index<BiomeMarkerLayerPrototype>(layer);

            foreach (var node in nodes)
            {
                if (modified.Contains(node) || HasAnchoredEntity(gridUid, grid, node))
                    continue;

                // Need to ensure the tile under it has loaded for anchoring.
                if (_mapSystem.GetTileRef(gridUid, grid, node).Tile.IsEmpty &&
                    TryGetTile(node, component.Layers, seed, (gridUid, grid), out var tile))
                {
                    _mapSystem.SetTile(gridUid, grid, node, tile.Value);
                }

                string? prototype;

                if (TryGetEntity(node, component, (gridUid, grid), out var proto) &&
                    layerProto.EntityMask.TryGetValue(proto, out var maskedProto))
                {
                    prototype = maskedProto;
                }
                else
                {
                    prototype = layerProto.Prototype;
                }

                if (prototype == null)
                    continue;

                // If it is a ghost role then purge it
                // TODO: This is *kind* of a bandaid but natural mobs spawns needs a lot more work.
                // Ideally we'd just have ghost role and non-ghost role variants for some stuff.
                var uid = EntityManager.CreateEntityUninitialized(prototype, _mapSystem.GridTileToLocal(gridUid, grid, node));
                RemComp<GhostTakeoverAvailableComponent>(uid);
                RemComp<GhostRoleComponent>(uid);
                EntityManager.InitializeAndStartEntity(uid);
                modified.Add(node);
            }
        }

        if (modified.Count == 0)
        {
            component.ModifiedTiles.Remove(chunk);
            _tilePool.Return(modified);
        }
        else
        {
            component.ModifiedTiles[chunk] = modified;
        }

        component.PendingMarkers.Remove(chunk);
    }
}
