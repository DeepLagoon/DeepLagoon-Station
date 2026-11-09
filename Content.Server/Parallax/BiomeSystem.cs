// Mono - Refactored into smaller subsystems
using System.Diagnostics;
using System.Numerics;
using Content.Server._DeepLagoon.Salvage;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared.Ghost;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Tag;
using Microsoft.Extensions.ObjectPool;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.Parallax;

public sealed partial class BiomeSystem : SharedBiomeSystem
{
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private IConsoleHost _console = default!;
    private SharedMapSystem _mapManager => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private DecalSystem _decals = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExpeditionLootSystem _expeditionLoot = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private TagSystem _tags = default!;

    private EntityQuery<BiomeComponent> _biomeQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<GhostComponent> _ghostQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private readonly HashSet<(EntityUid Viewer, int Priority)> _handledEntities = new();
    private const float DefaultLoadRange = 16f;
    private float _loadRange = DefaultLoadRange;
    private static readonly ProtoId<TagPrototype> AllowBiomeLoadingTag = "AllowBiomeLoading";

    private ObjectPool<HashSet<Vector2i>> _tilePool =
        new DefaultObjectPool<HashSet<Vector2i>>(new SetPolicy<Vector2i>(), 256);

    private float _updateTimer = UpdateInterval;
    private const float UpdateInterval = 1f / 10f;

    /// <summary>
    /// Load area for chunks containing tiles, decals etc.
    /// </summary>
    private Box2 _loadArea = new(-DefaultLoadRange, -DefaultLoadRange, DefaultLoadRange, DefaultLoadRange);

    /// <summary>
    /// Chunks near an observer, including a one-chunk buffer to avoid churn at the edge.
    /// </summary>
    private readonly Dictionary<BiomeComponent, HashSet<Vector2i>> _retainedChunks = new();
    private readonly Dictionary<ChunkRequest, (int ObserverPriority, float Distance)> _requestedChunks = new();
    private readonly PriorityQueue<ChunkRequest, (int ObserverPriority, float Distance)> _loadRequests = new();
    private readonly List<(ChunkRequest Request, (int ObserverPriority, float Distance) Priority)> _waitingForMarkers = new();
    private readonly Queue<ChunkRequest> _unloadRequests = new();
    private readonly Dictionary<EntityUid, (BiomeComponent Biome, Vector2i Tile)> _generatedEntityTiles = new();

    private const int LoadsPerTick = 4;
    private const int UnloadsPerTick = 8;
    private const int ObserverUnloadBacklog = 16;
    private static readonly TimeSpan LoadTimePerTick = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan UnloadTimePerTick = TimeSpan.FromMilliseconds(1);

    private readonly record struct ChunkRequest(EntityUid GridUid, BiomeComponent Biome, MapGridComponent Grid, Vector2i Chunk);

    private readonly Dictionary<BiomeComponent,
        Dictionary<string, HashSet<Vector2i>>> _markerChunks = new();

    public override void Initialize()
    {
        base.Initialize();
        Log.Level = LogLevel.Debug;
        _biomeQuery = GetEntityQuery<BiomeComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _ghostQuery = GetEntityQuery<GhostComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        SubscribeLocalEvent<BiomeComponent, MapInitEvent>(OnBiomeMapInit);
        SubscribeLocalEvent<BiomeComponent, ComponentShutdown>(OnBiomeShutdown);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<ShuttleFlattenEvent>(OnShuttleFlatten);
        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
        Subs.CVar(_configManager, CVars.NetMaxUpdateRange, SetLoadRange, true);
        InitializeChunkLoader();
        InitializeConfigManager();
        InitializePlanetSetup();
        InitializeCommands();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(ProtoReload);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        var targetMap = _transform.ToMapCoordinates(ev.TargetCoordinates);
        var targetMapUid = _mapSystem.GetMapOrInvalid(targetMap.MapId);

        if (!TryComp<BiomeComponent>(targetMapUid, out var biome))
            return;

        var preloadArea = new Vector2(32f, 32f);
        var targetArea = new Box2(targetMap.Position - preloadArea, targetMap.Position + preloadArea);
        Preload(targetMapUid, biome, targetArea);
    }

    private void OnEntityTerminating(ref EntityTerminatingEvent ev)
    {
        if (!_generatedEntityTiles.Remove(ev.Entity.Owner, out var generated))
            return;

        _scenerySpriteDefaults.Remove(ev.Entity.Owner);
        var (biome, tile) = generated;
        var chunk = SharedMapSystem.GetChunkIndices(tile, ChunkSize) * ChunkSize;
        if (biome.LoadedEntities.TryGetValue(chunk, out var entities) && entities.Remove(ev.Entity.Owner))
            biome.ModifiedTiles.GetOrNew(chunk).Add(tile);
    }

    private void OnBiomeShutdown(EntityUid uid, BiomeComponent biome, ComponentShutdown args)
    {
        foreach (var entities in biome.LoadedEntities.Values)
        {
            foreach (var ent in entities.Keys)
            {
                _generatedEntityTiles.Remove(ent);
                _scenerySpriteDefaults.Remove(ent);
            }
        }

        _markerChunks.Remove(biome);
        _sceneryDefaults.Remove(biome);
        CancelMarkerScan(biome);
    }

    private void OnShuttleFlatten(ref ShuttleFlattenEvent ev)
    {
        if (!TryComp<BiomeComponent>(ev.MapUid, out var biome) ||
            !TryComp<MapGridComponent>(ev.MapUid, out var grid))
        {
            return;
        }

        var tiles = new List<(Vector2i Index, Tile Tile)>();

        foreach (var aabb in ev.AABBs)
        {
            for (var x = Math.Floor(aabb.Left); x <= Math.Ceiling(aabb.Right); x++)
            {
                for (var y = Math.Floor(aabb.Bottom); y <= Math.Ceiling(aabb.Top); y++)
                {
                    var index = new Vector2i((int)x, (int)y);
                    var chunk = SharedMapSystem.GetChunkIndices(index, ChunkSize);

                    var mod = biome.ModifiedTiles.GetOrNew(chunk * ChunkSize);

                    if (!mod.Add(index) || !TryGetBiomeTile(index, biome.Layers, biome.Seed, (ev.MapUid, grid), out var tile))
                        continue;

                    // If we flag it as modified then the tile is never set so need to do it ourselves.
                    tiles.Add((index, tile.Value));
                }
            }
        }

        _mapSystem.SetTiles(ev.MapUid, grid, tiles);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _updateTimer += frameTime;
        if (_updateTimer >= UpdateInterval)
        {
            _updateTimer = 0f;
            RefreshChunkRequests();
        }

        _useStreamNoise = true;
        try
        {
            ProcessChunkRequests();
        }
        finally
        {
            _useStreamNoise = false;
            _streamNoise.Clear();
        }
    }

    private void RefreshChunkRequests()
    {
        _loadRequests.Clear();
        _unloadRequests.Clear();
        ProcessPlayerChunkRequests();
        QueueMarkerPreloads();

        foreach (var (request, priority) in _requestedChunks)
            _loadRequests.Enqueue(request, priority);

        _requestedChunks.Clear();
        _handledEntities.Clear();

        var biomes = AllEntityQuery<BiomeComponent, MapGridComponent>();
        while (biomes.MoveNext(out var gridUid, out var biome, out var grid))
        {
            if (!biome.Enabled || !biome.Running)
                continue;

            _retainedChunks.TryGetValue(biome, out var retained);
            foreach (var chunk in biome.LoadedChunks)
            {
                if (retained == null || !retained.Contains(chunk))
                    _unloadRequests.Enqueue(new ChunkRequest(gridUid, biome, grid, chunk));
            }
        }

        foreach (var retained in _retainedChunks.Values)
            _tilePool.Return(retained);

        _retainedChunks.Clear();
    }

    private void ProcessChunkRequests()
    {
        var unloaded = ProcessChunkUnloads();
        // Once the trail starts to accumulate, ghost streaming cannot add more chunks than we can remove.
        var ghostLoadsRemaining = _unloadRequests.Count > ObserverUnloadBacklog ? unloaded : LoadsPerTick;
        var start = Stopwatch.GetTimestamp();
        ProcessMarkerScan();
        var loaded = 0;
        for (var attempt = 0; attempt < 32 && loaded < LoadsPerTick &&
             (attempt == 0 || Stopwatch.GetElapsedTime(start) < LoadTimePerTick) &&
             _loadRequests.TryDequeue(out var request, out var priority); attempt++)
        {
            if (priority.ObserverPriority != 0 && ghostLoadsRemaining <= 0)
            {
                _loadRequests.Enqueue(request, priority);
                break;
            }

            var (gridUid, biome, grid, chunk) = request;
            if (biome.Enabled && biome.Running && !grid.Deleted && !TerminatingOrDeleted(gridUid))
            {
                var alreadyLoaded = biome.LoadedChunks.Contains(chunk);
                if (!alreadyLoaded && !PrepareChunkMarkers(biome, chunk))
                {
                    _waitingForMarkers.Add((request, priority));
                    continue;
                }

                LoadChunkMarkers(biome, gridUid, grid, chunk, biome.Seed);
                if (!alreadyLoaded)
                {
                    LoadChunk(biome, gridUid, grid, chunk, biome.Seed);
                    biome.LoadedChunks.Add(chunk);
                }

                loaded++;
                if (priority.ObserverPriority != 0)
                    ghostLoadsRemaining--;
            }
        }

        foreach (var (request, priority) in _waitingForMarkers)
            _loadRequests.Enqueue(request, priority);
        _waitingForMarkers.Clear();
    }

    private int ProcessChunkUnloads()
    {
        var unloaded = 0;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < UnloadsPerTick && _unloadRequests.TryDequeue(out var request); i++)
        {
            var (gridUid, biome, grid, chunk) = request;
            if (biome.Enabled && biome.Running && !grid.Deleted && !TerminatingOrDeleted(gridUid))
            {
                UnloadChunk(biome, gridUid, grid, chunk, biome.Seed, _chunkLoaderTiles);
                unloaded++;
            }

            if (Stopwatch.GetElapsedTime(start) >= UnloadTimePerTick)
                break;
        }

        return unloaded;
    }
}
