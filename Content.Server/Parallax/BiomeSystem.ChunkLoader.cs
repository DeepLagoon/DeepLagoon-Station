// Mono - Refactored into smaller subsystems
using System.Linq;
using Content.Server._DeepLagoon.Salvage;
using Content.Server.Spawners.EntitySystems;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Layers;
using Content.Shared.Sprite;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Noise;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;


namespace Content.Server.Parallax;

public sealed partial class BiomeSystem
{
    private readonly List<(Vector2i, Tile)> _chunkLoaderTiles = new();
    private readonly HashSet<EntityUid> _chunkOccupants = new();
    private readonly HashSet<Vector2i> _occupiedTiles = new();
    private readonly List<Vector2i> _preexistingTiles = new();
    private readonly Dictionary<(FastNoiseLite Noise, int Seed), FastNoiseLite> _streamNoise = new();
    private bool _useStreamNoise;
    [Dependency] private ConditionalSpawnerSystem _spawners = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    private readonly Dictionary<BiomeComponent, Dictionary<EntityPrototype, SceneryState>> _sceneryDefaults = new();
    private readonly Dictionary<EntityUid, RandomSpriteComponent> _scenerySpriteDefaults = new();

    private sealed record SceneryState(string Name, string Description, Angle Rotation, Dictionary<Type, IComponent> Components);

    // Pre-size data structures because malloc is still real
    private void InitializeChunkLoader()
    {
        var expectedChunkSize = ChunkSize * ChunkSize;
        _chunkLoaderTiles.Capacity = expectedChunkSize;
    }

    protected override FastNoiseLite GetNoise(FastNoiseLite seedNoise, int seed)
    {
        if (!_useStreamNoise)
            return base.GetNoise(seedNoise, seed);

        var key = (seedNoise, seed);
        if (!_streamNoise.TryGetValue(key, out var noise))
            _streamNoise[key] = noise = base.GetNoise(seedNoise, seed);

        return noise;
    }

    private void ForEachTileInChunk(Vector2i chunk, HashSet<Vector2i> modified, Action<Vector2i> action)
    {
        var startX = chunk.X;
        var startY = chunk.Y;
        var endX = startX + ChunkSize;
        var endY = startY + ChunkSize;

        for (var x = startX; x < endX; x++)
        {
            for (var y = startY; y < endY; y++)
            {
                var indices = new Vector2i(x, y);
                if (!modified.Contains(indices))
                    action(indices);
            }
        }
    }

    private bool HasAnchoredEntity(EntityUid gridUid, MapGridComponent grid, Vector2i indices)
    {
        var anchored = _mapSystem.GetAnchoredEntities(gridUid, grid, indices);
        return anchored.MoveNext(out _);
    }

    /// <summary>
    /// Loads a particular queued chunk for a biome.
    /// </summary>
    private IEnumerable<bool> LoadChunkSteps(ChunkRequest request, bool alreadyLoaded)
    {
        var (gridUid, component, grid, chunk) = request;
        var seed = component.Seed;
        foreach (var step in LoadChunkMarkers(component, gridUid, grid, chunk, seed))
            yield return step;
        if (alreadyLoaded)
            yield break;

        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= _tilePool.Get();
        _preexistingTiles.Clear();
        LoadTiles(component, gridUid, grid, chunk, seed, modified, _resumingChunks.Remove(request));
        // This buffer is shared with other chunks, so consume it before yielding.
        modified.UnionWith(_preexistingTiles);
        FinalizeChunk(component, chunk, modified);
        yield return true;

        if (component.Layers.Any(layer => layer is BiomeEntityLayer or BiomeMetaLayer))
        {
            foreach (var step in LoadEntities(component, gridUid, grid, chunk, seed))
                yield return step;
        }
        if (component.Layers.Any(layer => layer is BiomeDecalLayer or BiomeMetaLayer))
        {
            foreach (var step in LoadDecals(component, gridUid, grid, chunk, seed))
                yield return step;
        }
    }

    private void LoadTiles(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed,
        HashSet<Vector2i> modified,
        bool resuming = false)
    {
        _chunkLoaderTiles.Clear();

        ForEachTileInChunk(chunk, modified, indices =>
        {
            if (_mapSystem.TryGetTileRef(gridUid, grid, indices, out var tileRef) && !tileRef.Tile.IsEmpty)
            {
                if (!resuming || !TryGetTile(indices, component.Layers, seed, (gridUid, grid), out var expected) ||
                    tileRef.Tile != expected.Value)
                    _preexistingTiles.Add(indices);
                return;
            }

            if (TryGetTile(indices, component.Layers, seed, (gridUid, grid), out var biomeTile))
            {
                _chunkLoaderTiles.Add((indices, biomeTile.Value));
            }
        });

        if (_chunkLoaderTiles.Count > 0) // Don't need to call setTiles if there's nothing to load
        {
            _mapSystem.SetTiles(gridUid, grid, _chunkLoaderTiles);
            _chunkLoaderTiles.Clear();
        }
    }

    private IEnumerable<bool> LoadEntities(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed)
    {
        component.LoadedEntities.TryGetValue(chunk, out var loadedEntities);

        foreach (var indices in ChunkTiles(chunk))
        {
            yield return true;
            if (component.ModifiedTiles.TryGetValue(chunk, out var modified) && modified.Contains(indices) ||
                HasAnchoredEntity(gridUid, grid, indices))
                continue;

            if (TryGetEntity(indices, component, (gridUid, grid), out var entPrototype))
            {
                // Resolve rock tables directly and deterministically so a reload cannot reroll the ore.
                var terrainSeed = unchecked(seed * 397 ^ indices.X * 73856093 ^ indices.Y * 19349663);
                if (_spawners.ResolveBiomeTerrain(entPrototype, terrainSeed) is not { } prototype)
                    continue;

                var ent = _expeditionLoot.SpawnBiomeEntity(prototype, _mapSystem.GridTileToLocal(gridUid, grid, indices));
                if (TerminatingOrDeleted(ent))
                {
                    component.ModifiedTiles.GetOrNew(chunk).Add(indices);
                    continue;
                }

                if (_xformQuery.TryGetComponent(ent, out var xform) && !xform.Anchored)
                {
                    _transform.AnchorEntity((ent, xform), (gridUid, grid), indices);
                }

                if (loadedEntities == null)
                    component.LoadedEntities[chunk] = loadedEntities = new Dictionary<EntityUid, Vector2i>();
                loadedEntities.Add(ent, indices);
                _generatedEntityTiles.Add(ent, (component, indices));
                RecordSceneryDefault(component, ent);
            }
        }
    }

    private IEnumerable<bool> LoadDecals(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed)
    {
        component.LoadedDecals.TryGetValue(chunk, out var loadedDecals);

        foreach (var indices in ChunkTiles(chunk))
        {
            yield return true;
            if (component.ModifiedTiles.TryGetValue(chunk, out var modified) && modified.Contains(indices) ||
                HasAnchoredEntity(gridUid, grid, indices) || !TryGetDecals(indices, component.Layers, seed, (gridUid, grid), out var decals))
                continue;

            foreach (var decal in decals)
            {
                if (_decals.TryAddDecal(decal.ID, new EntityCoordinates(gridUid, decal.Position), out var dec))
                {
                    if (loadedDecals == null)
                        component.LoadedDecals[chunk] = loadedDecals = new Dictionary<uint, Vector2i>();
                    loadedDecals.Add(dec, indices);
                }
            }
        }
    }

    private void FinalizeChunk(BiomeComponent component, Vector2i chunk, HashSet<Vector2i> modified)
    {
        if (modified.Count == 0)
        {
            _tilePool.Return(modified);
            component.ModifiedTiles.Remove(chunk);
        }
        else
        {
            component.ModifiedTiles[chunk] = modified;
        }
    }

    /// <summary>
    /// Unloads a specific biome chunk.
    /// </summary>
    private IEnumerable<bool> UnloadChunkSteps(ChunkRequest request)
    {
        var (gridUid, component, grid, chunk) = request;
        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= _tilePool.Get();
        // Keep ownership of this set across ticks; tile changes during unloading
        // must update this same instance rather than overwrite it on completion.
        component.ModifiedTiles[chunk] = modified;
        try
        {
            UnloadDecals(component, gridUid, chunk, modified);
            yield return true;
            foreach (var step in UnloadEntities(component, gridUid, grid, chunk, modified))
                yield return step;
            UnloadTiles(component, gridUid, grid, chunk, component.Seed, modified, _chunkLoaderTiles);
            component.LoadedChunks.Remove(chunk);
        }
        finally
        {
            FinalizeChunk(component, chunk, modified);
        }
    }

    private static IEnumerable<Vector2i> ChunkTiles(Vector2i chunk)
    {
        for (var x = chunk.X; x < chunk.X + ChunkSize; x++)
        for (var y = chunk.Y; y < chunk.Y + ChunkSize; y++)
            yield return new Vector2i(x, y);
    }

    private void UnloadDecals(BiomeComponent component, EntityUid gridUid, Vector2i chunk, HashSet<Vector2i> modified)
    {
        if (!component.LoadedDecals.TryGetValue(chunk, out var loadedDecals))
            return;

        foreach (var (dec, indices) in loadedDecals)
        {
            if (!_decals.RemoveDecal(gridUid, dec))
                modified.Add(indices);
        }
        component.LoadedDecals.Remove(chunk);
    }

    private IEnumerable<bool> UnloadEntities(BiomeComponent component, EntityUid gridUid, MapGridComponent grid, Vector2i chunk, HashSet<Vector2i> modified)
    {
        if (!component.LoadedEntities.TryGetValue(chunk, out var loadedEntities))
            yield break;

        var xformQuery = GetEntityQuery<TransformComponent>();

        // Batch validate entities and collect ones to delete
        foreach (var (ent, tile) in loadedEntities.ToArray())
        {
            yield return true;
            loadedEntities.Remove(ent);
            _generatedEntityTiles.Remove(ent);
            _scenerySpriteDefaults.Remove(ent, out var initialSprite);
            if (Deleted(ent) || !xformQuery.TryGetComponent(ent, out var xform))
            {
                modified.Add(tile);
                continue;
            }

            if (!xform.Anchored || xform.ParentUid != gridUid ||
                _mapSystem.LocalToTile(gridUid, grid, xform.Coordinates) != tile)
            {
                modified.Add(tile);
                continue;
            }

            if (!IsUnchangedScenery(component, ent, xform, initialSprite))
            {
                modified.Add(tile);
                continue;
            }

            // Validate and delete together: players can modify or move scenery
            // between slices, so never retain an unchecked deletion for a later tick.
            Del(ent);
        }

        component.LoadedEntities.Remove(chunk);

    }

    private void RecordSceneryDefault(BiomeComponent biome, EntityUid uid)
    {
        var meta = MetaData(uid);
        if (meta.EntityPrototype is not { } prototype || !_expeditionLoot.IsBiomeScenery(uid))
            return;

        // RandomSprite selects a different visual during MapInit for each instance.
        if (TryComp<RandomSpriteComponent>(uid, out var sprite))
            _scenerySpriteDefaults[uid] = _serialization.CreateCopy(sprite, skipHook: true, notNullableOverride: true);

        if (!_sceneryDefaults.TryGetValue(biome, out var defaults))
            _sceneryDefaults[biome] = defaults = new Dictionary<EntityPrototype, SceneryState>();
        if (defaults.ContainsKey(prototype))
            return;

        // Generated scenery can have initialized tags and other data absent from its YAML.
        // Keep one post-startup baseline per prototype and biome.
        var components = new Dictionary<Type, IComponent>();
        foreach (var comp in AllComps(uid))
        {
            if (comp is MetaDataComponent or TransformComponent or ExpeditionLootOriginComponent)
                continue;

            components.Add(comp.GetType(), (IComponent) _serialization.CreateCopy((object) comp, skipHook: true)!);
        }
        defaults.Add(prototype, new SceneryState(meta.EntityName, meta.EntityDescription, Transform(uid).LocalRotation, components));
    }

    private bool IsUnchangedScenery(BiomeComponent biome, EntityUid uid, TransformComponent xform, RandomSpriteComponent? initialSprite)
    {
        var meta = MetaData(uid);
        if (meta.EntityPrototype is not { } prototype || !_sceneryDefaults.TryGetValue(biome, out var defaults) ||
            !defaults.TryGetValue(prototype, out var initial))
            return EntityManager.IsDefault(uid);

        if (xform.LocalRotation != initial.Rotation || meta.EntityName != initial.Name || meta.EntityDescription != initial.Description)
            return false;

        // Metadata and transform are checked above. Legacy scenery can still have the loot origin stamp.
        var componentCount = EntityManager.ComponentCount(uid) - (HasComp<ExpeditionLootOriginComponent>(uid) ? 3 : 2);
        if (componentCount != initial.Components.Count)
            return false;

        foreach (var (type, original) in initial.Components)
        {
            var baseline = original is RandomSpriteComponent && initialSprite != null ? initialSprite : original;
            if (!EntityManager.TryGetComponent(uid, type, out var comp) || !SceneryComponentEquals(comp, baseline))
                return false;
        }

        return true;
    }

    private bool SceneryComponentEquals(IComponent component, IComponent original)
    {
        // Tags are a set. Two copies with identical tags must compare equal.
        if (component is TagComponent tags && original is TagComponent originalTags)
            return tags.Tags.Count == originalTags.Tags.Count && _tags.HasAllTags(tags, originalTags.Tags);

        return _serialization.DataFieldEquals(component.GetType(), component, original);
    }

    private void UnloadTiles(BiomeComponent component, EntityUid gridUid, MapGridComponent grid, Vector2i chunk, int seed, HashSet<Vector2i> modified, List<(Vector2i, Tile)> tiles)
    {
        _chunkOccupants.Clear();
        _occupiedTiles.Clear();
        var chunkEnd = chunk + new Vector2i(ChunkSize, ChunkSize);
        var bounds = new Box2(chunk * grid.TileSize, chunkEnd * grid.TileSize);
        _lookup.GetLocalEntitiesIntersecting(gridUid, bounds, _chunkOccupants,
            LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Sundries);
        foreach (var ent in _chunkOccupants)
        {
            if (_xformQuery.TryGetComponent(ent, out var xform) && xform.ParentUid == gridUid && !_ghostQuery.HasComp(ent))
                _occupiedTiles.Add(_mapSystem.LocalToTile(gridUid, grid, xform.Coordinates));
        }

        ForEachTileInChunk(chunk, modified, indices =>
        {
            if (_occupiedTiles.Contains(indices) || HasAnchoredEntity(gridUid, grid, indices))
            {
                modified.Add(indices);
                return;
            }

            if (!TryGetBiomeTile(indices, component.Layers, seed, null, out var biomeTile))
                return;

            var tileRef = _mapSystem.GetTileRef(gridUid, grid, indices);
            if (tileRef.Tile != biomeTile.Value)
            {
                modified.Add(indices);
                return;
            }

            tiles.Add((indices, Tile.Empty));
        });

        // Batch tile removal
        if (tiles.Count > 0)
        {
            _mapSystem.SetTiles(gridUid, grid, tiles);
            tiles.Clear();
        }
    }
}
