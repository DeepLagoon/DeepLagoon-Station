using System.IO;
using System.Numerics;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Content.Shared.Station.Components;
using Robust.Shared.Prototypes;
using StarMapSystem = Content.Server._FarHorizons.StarSystem.StarSystemMapSystem;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public sealed class SectorSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly MapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    private SectorStore? _store;
    private readonly Dictionary<(string Campaign, string Id), EntityUid> _loaded = new();
    public int LoadedCount => _loaded.Count;
    public string Campaign => _cfg.GetCVar(SpaceEnvironmentCVars.Campaign);
    public bool Enabled => _cfg.GetCVar(SpaceEnvironmentCVars.Enabled);
    public SectorStore Store => _store ??= new SectorStore(_resources.UserData.RootDir is { } root
        ? Path.Combine(root, "space-sectors.db") : ":memory:");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StarSystemAssignedEvent>(OnSystemAssigned);
        SubscribeLocalEvent<SpaceEnvironmentComponent, ComponentShutdown>(OnSectorShutdown);
    }

    public override void Shutdown()
    {
        _loaded.Clear();
        _store?.Dispose();
        _store = null;
        base.Shutdown();
    }

    private void OnSystemAssigned(ref StarSystemAssignedEvent args)
    {
        if (!Enabled || HasComp<SpaceEnvironmentComponent>(args.Map)) return;
        var prototype = _prototypes.Index<StarSystemPrototype>(args.System);
        var profile = _prototypes.Index<SectorProfilePrototype>("DeepLagoonFrontier");
        var id = "fixed/" + args.System;
        if (_loaded.TryGetValue((Campaign, id), out var loaded) && loaded != args.Map && !TerminatingOrDeleted(loaded)) return;
        var definition = Store.Load(Campaign, id);
        if (definition == null)
        {
            definition = SectorGenerator.FromSystem(id,
                SectorRandom.Derive(unchecked((uint) _cfg.GetCVar(SpaceEnvironmentCVars.Seed)), id), args.System, prototype, profile);
            ExcludeInitialStations(args.Map, definition);
            definition = Store.GetOrCreate(Campaign, definition);
        }
        definition = ExpandRegions(definition, args.Map);
        Bind(args.Map, definition);
    }

    /// <summary>Idempotent generation API for future sector travel. No runtime map IDs enter the save.</summary>
    public SectorDefinition GetOrCreate(string id, uint seed, string profile)
    {
        SectorStore.ValidateKey(id);
        var existing = Store.Load(Campaign, id);
        if (existing != null) return ExpandRegions(existing);
        return Store.GetOrCreate(Campaign, SectorGenerator.Generate(id, seed, _prototypes.Index<SectorProfilePrototype>(profile)));
    }

    public EntityUid Create(string id, uint seed, string profile)
    {
        if (_loaded.TryGetValue((Campaign, id), out var loaded) && !TerminatingOrDeleted(loaded)) return loaded;
        if (_loaded.Count >= Math.Clamp(_cfg.GetCVar(SpaceEnvironmentCVars.MaxLoaded), 1, 64))
            throw new InvalidOperationException("Loaded sector capacity reached. Transfer ships and unload an empty sector first.");
        var definition = GetOrCreate(id, seed, profile);
        ValidatePrototypes(definition);
        var map = _maps.CreateMap(out _);
        Bind(map, definition);
        return map;
    }

    public void Bind(EntityUid map, SectorDefinition definition)
    {
        ValidatePrototypes(definition);
        if (_loaded.TryGetValue((Campaign, definition.Id), out var existing) && existing != map && !TerminatingOrDeleted(existing))
            throw new InvalidOperationException("This sector already has a loaded map.");
        if (!HasComp<SpaceEnvironmentComponent>(map)
            && _loaded.Count >= Math.Clamp(_cfg.GetCVar(SpaceEnvironmentCVars.MaxLoaded), 1, 64))
            throw new InvalidOperationException("Loaded sector capacity reached.");
        var environment = EnsureComp<SpaceEnvironmentComponent>(map);
        if (environment.Campaign.Length > 0) _loaded.Remove((environment.Campaign, environment.Definition.Id));
        environment.Campaign = Campaign;
        environment.Definition = definition;
        _loaded[(Campaign, definition.Id)] = map;
        environment.Surveyed = Store.Surveys(Campaign, definition.Id);
        Dirty(map, environment);
        var star = EnsureComp<StarSystemMapComponent>(map);
        star.Definition = definition;
        EntityManager.System<StarMapSystem>().SetSystem((map, star), star.System ?? "SystemKyphrus");
        EnsureComp<Content.Shared.Shuttles.Components.FTLDestinationComponent>(map);
        EntityManager.System<MetaDataSystem>().SetEntityName(map, definition.Id);
        EntityManager.System<SpaceEnvironmentSystem>().Invalidate(map);
    }

    /// <summary>Runtime eviction is safe only after the future transfer layer has moved every grid and player.</summary>
    public bool TryUnload(EntityUid map)
    {
        if (!HasComp<SpaceEnvironmentComponent>(map)) return false;
        var players = AllEntityQuery<ActorComponent, TransformComponent>();
        while (players.MoveNext(out _, out _, out var transform))
            if (transform.MapUid == map) return false;
        EntityManager.System<OrbitalDebrisSystem>().ClearUnclaimed(map);
        var grids = AllEntityQuery<MapGridComponent, TransformComponent>();
        while (grids.MoveNext(out _, out _, out var transform))
            if (transform.MapUid == map) return false;
        Del(map);
        return true;
    }

    public bool Survey(EntityUid map, string region)
    {
        if (!TryComp<SpaceEnvironmentComponent>(map, out var environment)
            || !Store.TrySurvey(environment.Campaign, environment.Definition.Id, region)) return false;
        environment.Surveyed = Store.Surveys(environment.Campaign, environment.Definition.Id);
        Dirty(map, environment);
        return true;
    }

    /// <summary>Unbounded logical coordinates; only requested sectors are materialized.</summary>
    public SectorDefinition GetOrCreateAt(long x, long y, string profile)
    {
        var id = FormattableString.Invariant($"sector/{x}/{y}");
        var seed = SectorRandom.Derive(unchecked((uint) _cfg.GetCVar(SpaceEnvironmentCVars.Seed)), id);
        return GetOrCreate(id, seed, profile);
    }

    private void OnSectorShutdown(EntityUid uid, SpaceEnvironmentComponent component, ComponentShutdown args)
    {
        _loaded.Remove((component.Campaign, component.Definition.Id));
        EntityManager.System<SpaceEnvironmentSystem>().RemoveSector(uid);
        EntityManager.System<OrbitalDebrisSystem>().ForgetMap(uid);
    }

    private SectorDefinition ExpandRegions(SectorDefinition original, EntityUid? map = null)
    {
        var profile = _prototypes.Index<SectorProfilePrototype>(original.Profile);
        if (original.Regions.Count >= profile.RegionCount) return original;
        var expanded = SectorGenerator.ExpandRegions(original, profile);
        var additions = expanded.Regions.GetRange(original.Regions.Count, expanded.Regions.Count - original.Regions.Count);
        // Clear only new areas around inhabited grids; never move or remove an old discovery.
        expanded.Regions = additions;
        if (map is { } mapUid) ExcludeInitialStations(mapUid, expanded);
        return additions.Count == 0 ? original : Store.AppendRegions(Campaign, original, additions);
    }

    private void ExcludeInitialStations(EntityUid map, SectorDefinition definition)
    {
        var query = AllEntityQuery<StationDataComponent>();
        while (query.MoveNext(out _, out var station))
        foreach (var grid in station.Grids)
        {
            if (!TryComp<TransformComponent>(grid, out var transform) || transform.MapUid != map
                || !TryComp<MapGridComponent>(grid, out var geometry)) continue;
            var center = _transform.GetWorldPosition(transform);
            var clearance = geometry.LocalAABB.Size.Length() * 0.5f + 800f;
            definition.Regions.RemoveAll(region => Vector2.Distance(center, new Vector2(region.X, region.Y)) < region.Radius + clearance);
        }
    }

    private void ValidatePrototypes(SectorDefinition definition)
    {
        SectorStore.Validate(definition);
        if (!_prototypes.HasIndex<StarTypePrototype>(definition.Star)
            || !_prototypes.HasIndex<SectorProfilePrototype>(definition.Profile))
            throw new InvalidDataException("Unknown saved sector star/profile. Restore its prototypes before loading.");
        foreach (var planet in definition.Planets)
            if (!_prototypes.HasIndex<PlanetTypePrototype>(planet.Template))
                throw new InvalidDataException($"Unknown saved planet template {planet.Template}.");
    }
}
