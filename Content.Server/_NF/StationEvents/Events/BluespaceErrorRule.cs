using Content.Server._DeepLagoon.Mapping;
using Content.Server._DeepLagoon.SpaceEnvironment;
using System.Numerics;
using Content.Server.Cargo.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.StationEvents.Components;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Random;
using Content.Server._NF.Salvage;
using Content.Server._NF.Bank;
using Content.Shared._NF.Bank.BUI;
using Content.Server.GameTicking;
using Content.Server.Procedural;
using Robust.Shared.Prototypes;
using Content.Shared.Salvage;
using Content.Server.Maps.NameGenerators;
using Content.Server.StationEvents.Events;
using Content.Server._NF.Station.Systems;
using Content.Server._NF.StationEvents.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.EntitySerialization;
using Content.Server._Mono.StationEvents;
using Content.Server._Mono.GridClaimer;
using Content.Server._Mono.Store.Components;
using Content.Server._Mono.Store;
using Newtonsoft.Json;
using Robust.Shared.Configuration;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NF.StationEvents.Events;

public sealed partial class BluespaceErrorRule : StationEventSystem<BluespaceErrorRuleComponent>
{
    private const float MinimumGreenZoneDistance = 4000f;
    private const int PlacementAttempts = 10;

    private ISawmill _performanceLog = default!;

    NanotrasenNameGenerator _nameGenerator = new();
    private SharedMapSystem _mapManager => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private MapSystem _map = default!;
    private SharedMapSystem _mapSystem => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private DungeonSystem _dungeon = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private LinkedLifecycleGridSystem _linkedLifecycleGrid = default!;
    [Dependency] private StationRenameWarpsSystems _renameWarps = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private SharedSalvageSystem _salvage = default!;
    [Dependency] private AutoExtendRuleSystem _autoExtend = default!;
    [Dependency] private CurrencyInjectionSystem _currencyInjection = default!;
    [Dependency] private IConfigurationManager _performanceConfig = default!;

    public override void Initialize()
    {
        base.Initialize();
        _performanceLog = Logger.GetSawmill("bluespace.performance");
    }

    private BluespaceEventTiming? GetEventTiming(EntityUid rule)
    {
        var id = MetaData(rule).EntityPrototype?.ID;
        if (id is null || (id != "BluespaceSalvage" && !id.StartsWith("BluespaceDungeon", StringComparison.Ordinal)))
            return null;

        return new BluespaceEventTiming(_performanceLog, id, rule, Timing.TickPeriod,
            _performanceConfig.GetCVar(Robust.Shared.CVars.PerformanceLogLevel) > 1);
    }

    protected override void Started(EntityUid uid, BluespaceErrorRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        QueueEventLoad(uid, component);
    }

    private IEnumerable<object?> SpawnEventSteps(EntityUid uid, BluespaceErrorRuleComponent component)
    {
        if (!_map.TryGetMap(GameTicker.DefaultMap, out var mapUid))
            yield break;

        var timing = GetEventTiming(uid);
        var mapOrigin = new EntityCoordinates(mapUid.Value, Vector2.Zero);
        var needsStartingValue = HasComp<CurrencyInjectionOnBluespaceErrorComponent>(uid);


        foreach (var group in component.Groups.Values)
        {
            var count = _random.Next(group.MinCount, group.MaxCount + 1);

            for (var i = 0; i < count; i++)
            {
                EntityUid spawned;

                switch (group)
                {
                    case BluespaceDungeonSpawnGroup dungeon:
                        if (!TryDungeonSpawn(mapOrigin, uid, component, dungeon, i, timing, out spawned))
                            continue;

                        break;
                    case BluespaceGridSpawnGroup grid:
                        // A dedicated pre-mapinit staging map isolates each incomplete grid.
                        var stage = _map.CreateMap(out var stagingId, runMapInit: false);
                        component.MapsUid.Add(stagingId);
                        Entity<MapGridComponent>? loaded = null;
                        foreach (var step in LoadEventGrid(uid, stagingId, grid, i, timing, value => loaded = value))
                            yield return step;
                        if (loaded == null || !HasComp<ShuttleComponent>(loaded.Value) || !TryPlaceGrid(loaded.Value, mapOrigin, grid))
                        {
                            EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>().Queue(stage);
                            continue;
                        }
                        spawned = loaded.Value;
                        if (grid.NameGrid) _metadata.SetEntityName(spawned, GetLastSelectedPath(grid).FilenameWithoutExtension);
                        EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>().Queue(stage);
                        break;
                    default:
                        throw new NotImplementedException();
                }

                if (group.NameLoc != null && group.NameLoc.Count > 0)
                {
                    _metadata.SetEntityName(spawned, Loc.GetString(_random.Pick(group.NameLoc)));

                }
                else if (_protoManager.TryIndex(group.NameDataset, out var dataset))
                {
                    string gridName;
                    switch (group.NameDatasetType)
                    {
                        case BluespaceDatasetNameType.FTL:
                            gridName = _salvage.GetFTLName(dataset, _random.Next());
                            break;
                        case BluespaceDatasetNameType.Nanotrasen:
                            gridName = _nameGenerator.FormatName(Loc.GetString(_random.Pick(dataset.Values)) + " {1}"); // We need the prefix.
                            break;
                        case BluespaceDatasetNameType.Verbatim:
                        default:
                            gridName = Loc.GetString(_random.Pick(dataset.Values));
                            break;
                    }

                    _metadata.SetEntityName(spawned, gridName);
                }

                if (group.NameWarp)
                {
                    bool? adminOnly = group.HideWarp ? true : null;
                    _renameWarps.SyncWarpPointsToGrid(spawned, forceAdminOnly: adminOnly);
                }

                EntityManager.AddComponents(spawned, group.AddComponents);

                component.GridsUid.Add(spawned);
                if (needsStartingValue)
                    component.StartingValue += _pricing.AppraiseGrid(spawned);

                if (component.ExtendIfPopulated && group is BluespaceGridSpawnGroup)
                    _autoExtend.AutoExtend(uid, spawned);
                yield return null;
            }
        }


    }

    private bool TryPlaceGrid(EntityUid grid, EntityCoordinates mapOrigin, IBluespaceSpawnGroup group)
    {
        if (!Exists(mapOrigin.EntityId))
            return false;

        var mapId = _transform.GetMapId(mapOrigin);
        var zoneCenters = new List<Vector2>();
        var zones = AllEntityQuery<GreenZoneComponent, MapGridComponent, TransformComponent>();
        while (zones.MoveNext(out var uid, out var zone, out var zoneGrid, out var transform))
        {
            if (transform.MapID != mapId || zone.Radius <= 0f || TerminatingOrDeleted(uid))
                continue;

            var center = _transform.ToMapCoordinates(new EntityCoordinates(uid, zoneGrid.LocalAABB.Center));
            zoneCenters.Add(center.Position);
        }

        if (zoneCenters.Count == 0)
        {
            var offset = group.MinimumDistance > 0f
                ? _random.NextVector2(group.MinimumDistance, group.MaximumDistance)
                : Vector2.Zero;
            return _shuttle.TryFTLProximity(grid, mapOrigin.Offset(offset));
        }

        var minimum = MathF.Max(group.MinimumDistance, MinimumGreenZoneDistance);
        var maximum = group.MaximumDistance;
        // Shift short ranges outward when the new minimum would leave no room to scatter.
        if (maximum <= minimum)
            maximum = minimum + MathF.Max(1f, group.MaximumDistance - group.MinimumDistance);

        var origin = _transform.ToCoordinates(new MapCoordinates(zoneCenters[0], mapId));

        bool HasClearance(MapCoordinates point)
        {
            foreach (var center in zoneCenters)
            {
                if (Vector2.DistanceSquared(point.Position, center) < MinimumGreenZoneDistance * MinimumGreenZoneDistance)
                    return false;
            }

            return true;
        }

        for (var i = 0; i < PlacementAttempts; i++)
        {
            var target = origin.Offset(_random.NextVector2(minimum, maximum));
            if (_shuttle.TryFTLProximity(grid, target, HasClearance))
                return true;
        }

        Log.Warning($"Could not place bluespace event grid {ToPrettyString(grid)} clear of the Green Zone.");
        return false;
    }

    private bool TryDungeonSpawn(EntityCoordinates mapOrigin, EntityUid ruleUid, BluespaceErrorRuleComponent component,
        BluespaceDungeonSpawnGroup group, int i, BluespaceEventTiming? timing, out EntityUid spawned)
    {
        spawned = EntityUid.Invalid;

        // handle empty prototype list, _random.Pick throws
        if (group.Protos.Count <= 0)
            return false;

        // Enforce randomness with some round-robin-ish behaviour
        int maxIndex = group.Protos.Count - (i % group.Protos.Count);
        int index = _random.Next(maxIndex);
        var dungeonProtoId = group.Protos[index];
        // Move selected item to the end of the list
        group.Protos.RemoveAt(index);
        group.Protos.Add(dungeonProtoId);

        if (!_protoManager.TryIndex(dungeonProtoId, out var dungeonProto))
        {
            return false;
        }

        _mapSystem.CreateMap(out var mapId);

        var spawnedGrid = _mapManager.CreateGridEntity(mapId);

        _transform.SetMapCoordinates(spawnedGrid, new MapCoordinates(Vector2.Zero, mapId));
        _dungeon.GenerateDungeon(dungeonProto, dungeonProto.ID, spawnedGrid.Owner, spawnedGrid.Comp, Vector2i.Zero, _random.Next(), grid =>
        {
            if (TerminatingOrDeleted(ruleUid) || !GameTicker.IsGameRuleActive(ruleUid))
                return false;
            if (TryPlaceGrid(grid, mapOrigin, group))
            {
                if (component.ExtendIfPopulated)
                    _autoExtend.AutoExtend(ruleUid, grid);

                return true;
            }

            component.GridsUid.Remove(grid);
            return false;
        }, timing);

        spawned = spawnedGrid.Owner;
        component.MapsUid.Add(mapId);
        return true;
    }

    protected override void Ended(EntityUid uid, BluespaceErrorRuleComponent component, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        CancelEventLoad(uid);
        base.Ended(uid, component, gameRule, args);

        if (component.GridsUid == null)
            return;

        var timing = GetEventTiming(uid);
        foreach (var componentGridUid in component.GridsUid)
        {
            if (!TryComp(componentGridUid, out TransformComponent? gridTransform))
            {
                Log.Error("bluespace error objective was missing transform component");
                continue;
            }

            if (gridTransform.GridUid is not EntityUid gridUid)
            {
                Log.Error("bluespace error has no associated grid?");
                continue;
            }

            // don't delete it if claimed
            if (TryComp<ClaimableGridComponent>(componentGridUid, out var claimable) && claimable.Claimed)
                continue;

            // A published root can also be listed by another event or by a linked family.
            // Its retirement map owns cleanup now; do not appraise/reward it a second time.
            if (IsGridRetiring(gridTransform))
                continue;

            if (component.DeleteGridsOnEnd)
            {
                var cleanupStart = timing != null ? Stopwatch.GetTimestamp() : 0;
                // Handle mobrestrictions getting deleted
                var query = AllEntityQuery<NFSalvageMobRestrictionsComponent>();

                while (query.MoveNext(out var salvUid, out var salvMob))
                {
                    if (!salvMob.DespawnIfOffLinkedGrid)
                    {
                        var transform = Transform(salvUid);
                        if (transform.GridUid != salvMob.LinkedGridEntity)
                        {
                            RemComp<NFSalvageMobRestrictionsComponent>(salvUid);
                            continue;
                        }
                    }

                    if (gridTransform.GridUid == salvMob.LinkedGridEntity)
                    {
                        QueueDel(salvUid);
                    }
                }

                var retirement = PrepareGridRetirement(gridUid);

                double gridValue = 0;
                try
                {
                    var injectCurrency = TryComp<CurrencyInjectionOnBluespaceErrorComponent>(uid, out var comp);
                    gridValue = injectCurrency || component.RewardAccounts.Count != 0 ? _pricing.AppraiseGrid(gridUid) : 0;
                    // Mono: currency injections
                    if (comp != null)
                    {
                        var childQuery = Transform(gridUid).ChildEnumerator;
                        var requiredEntitiesFound = true;
                        foreach (var entProtoId in comp.RequiredEntities)
                        {
                            requiredEntitiesFound = false;
                            while (childQuery.MoveNext(out var entity))
                            {
                                var metaData = MetaData(entity);
                                if (metaData.EntityPrototype != null && entProtoId.Equals(metaData.EntityPrototype.ID))
                                {
                                    requiredEntitiesFound = true;
                                    break;
                                }
                            }
                            if (!requiredEntitiesFound)
                                break;
                        }
                        if (gridValue / component.StartingValue > comp.IntegrityRequirement && requiredEntitiesFound) // good job!
                            _currencyInjection.InjectCurrency(comp.Company, comp.Amount);
                    }
                    // Mono end

                    // Retire the entire linked family before restoring players. Keeping an
                    // expiring grid on the public map would let traversal attach them again.
                    RetirePublishedGrid(retirement, timing != null);
                }
                finally
                {
                    RestoreRetirementPlayers(retirement);
                }

                foreach (var (account, rewardCoeff) in component.RewardAccounts)
                {
                    var reward = (int)(gridValue * rewardCoeff);
                    _bank.TrySectorDeposit(account, reward, LedgerEntryType.BluespaceReward);
                }

                timing?.GridRetirement(gridUid, retirement.Map, retirement.Grids.Count, retirement.Players.Count,
                    cleanupStart, retirement.Start, retirement.MapCreated, retirement.Paused, retirement.Moved,
                    retirement.LifecycleReleased, retirement.Queued, Stopwatch.GetTimestamp());
            }
        }

        foreach (MapId mapId in component.MapsUid)
        {
            if (!_map.TryGetMap(mapId, out var mapUid))
                continue;

            var deleteStart = timing != null ? Stopwatch.GetTimestamp() : 0;
            EntityManager.System<Content.Server._DeepLagoon.Performance.StagedMapDeletionSystem>().Queue(mapUid.Value);
            timing?.Deletion("queue_dungeon_map_deletion", mapUid.Value, deleteStart);
        }
    }
}
