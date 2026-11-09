// Mono - Refactored into smaller subsystems
using System.Numerics;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using ChunkIndicesEnumerator = Robust.Shared.Map.Enumerators.ChunkIndicesEnumerator;

namespace Content.Server.Parallax;

public sealed partial class BiomeSystem
{
    /// <summary>
    /// Processes all players and viewers to determine which chunks need to be loaded.
    /// </summary>
    private void ProcessPlayerChunkRequests()
    {
        foreach (var pSession in Filter.GetAllPlayers(_playerManager))
        {
            var ghost = _ghostQuery.HasComp(pSession.AttachedEntity);
            if (pSession.AttachedEntity is { } attached)
                RequestChunksForViewer(attached, ghost);

            foreach (var viewer in pSession.ViewSubscriptions)
                RequestChunksForViewer(viewer, ghost);
        }
    }

    private void RequestChunksForViewer(EntityUid viewer, bool ghostSession)
    {
        if (!_xformQuery.TryGetComponent(viewer, out var xform) ||
            xform.MapUid is not { } mapUid ||
            !_biomeQuery.TryGetComponent(mapUid, out var biome) ||
            !biome.Enabled || !biome.Running ||
            !TryComp<MapGridComponent>(mapUid, out var grid))
        {
            return;
        }

        var ghost = _ghostQuery.HasComp(viewer);
        if (ghost && !_tags.HasTag(viewer, AllowBiomeLoadingTag))
            return;

        var priority = ghostSession || ghost ? 1 : 0;
        if (!_handledEntities.Add((viewer, priority)))
            return;

        var position = _transform.GetWorldPosition(xform);
        AddChunksInRange(mapUid, biome, grid, position, priority);
        foreach (var layer in biome.ForcedMarkerLayers)
            AddMarkerChunksInRange(biome, position, ProtoManager.Index<BiomeMarkerLayerPrototype>(layer));
    }

    private void AddChunksInRange(EntityUid mapUid, BiomeComponent biome, MapGridComponent grid, Vector2 position, int observerPriority)
    {
        if (!_retainedChunks.TryGetValue(biome, out var retained))
        {
            retained = _tilePool.Get();
            _retainedChunks.Add(biome, retained);
        }

        var area = _loadArea.Translated(position);
        var retainedChunks = new ChunkIndicesEnumerator(area.Enlarged(ChunkSize), ChunkSize);
        while (retainedChunks.MoveNext(out var retainedChunk))
            retained.Add(retainedChunk.Value * ChunkSize);

        var enumerator = new ChunkIndicesEnumerator(area, ChunkSize);
        while (enumerator.MoveNext(out var chunkOrigin))
        {
            var chunk = chunkOrigin.Value * ChunkSize;
            if (biome.LoadedChunks.Contains(chunk) && !biome.PendingMarkers.ContainsKey(chunk))
                continue;

            var request = new ChunkRequest(mapUid, biome, grid, chunk);
            var priority = (observerPriority, Vector2.DistanceSquared(position, chunk + new Vector2(ChunkSize / 2f)));
            if (!_requestedChunks.TryGetValue(request, out var current) || priority.CompareTo(current) < 0)
                _requestedChunks[request] = priority;
        }
    }

    private void AddMarkerChunksInRange(BiomeComponent biome, Vector2 worldPos, IBiomeMarkerLayer layer)
    {
        // Offset the load area so it's centralised.
        var loadArea = new Box2(0, 0, layer.Size, layer.Size);
        var halfLayer = new Vector2(layer.Size / 2f);

        var enumerator = new ChunkIndicesEnumerator(loadArea.Translated(worldPos - halfLayer), layer.Size);

        while (enumerator.MoveNext(out var chunkOrigin))
        {
            var lay = _markerChunks.GetOrNew(biome).GetOrNew(layer.ID);
            lay.Add(chunkOrigin.Value * layer.Size);
        }
    }
}
