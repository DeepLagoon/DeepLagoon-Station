using System.IO;
using System.Linq;
using System.Numerics;
using System.Diagnostics;
using System.Threading.Tasks;
using Content.Server._Crescent.Hardpoint;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Power.Generator;
using Content.Server.Research.Systems;
using Content.Server.Salvage;
using Content.Server.Ame.Components;
using Content.Server.Ame.EntitySystems;
using Content.Server.Lathe;
using Content.Server.Materials;
using Content.Server._Mono.FireControl;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Clothing.Components;
using Content.Shared.Construction.Components;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind.Components;
using Content.Shared.Item;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Storage;
using Content.Shared.Stacks;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Components;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private const int ExclusionPreviewLimit = 500;

    [Dependency] private IDependencyCollection _dependencies = default!;
    [Dependency] private IComponentFactory _components = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private HardpointSystem _hardpoints = default!;
    [Dependency] private ConstructionSystem _construction = default!;
    [Dependency] private UpgradePowerSystem _upgradePower = default!;
    [Dependency] private GeneratorSystem _generators = default!;
    [Dependency] private DeviceLinkSystem _deviceLinks = default!;
    [Dependency] private DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private ResearchSystem _research = default!;
    [Dependency] private SalvageSystem _salvage = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    private static readonly HashSet<string> TransientComponents =
    [
        "ShuttleDeed", "ShipGuestAccess", "ShipOwnership", "StationMember", "LinkedLifecycleGridParent",
        "LinkedLifecycleGridChild", "ShipShielded", "FireControlGrid",
    ];
    private static readonly HashSet<string> MeleeWeaponRoots =
    [
        "BaseKnife", "BaseSword", "BaseMeleeWeaponEnergy", "BaseWeaponCrusher", "Spear", "Sledgehammer",
        "Mjollnir", "SingularityHammer", "BaseBallBat", "FireAxe", "CaneBlade", "ArmBlade", "Stunbaton",
        "Stunprod", "RitualDagger", "EldritchBlade", "UnholyHalberd", "Gohei", "Chainsaw",
    ];
    private bool HasNamed(EntityUid entity, string name) => _components.TryGetRegistration(name, out var type) && EntityManager.HasComponent(entity, type.Type);
    private bool Original(EntityUid entity, EntityUid grid)
    {
        var ship = Comp<ParkingShuttleComponent>(grid);
        return TryComp<ParkingOriginalComponent>(entity, out var original) && original.Shuttle == ship.Identity
            && ship.OriginalItems.TryGetValue(original.Identity, out var registered) && registered == entity;
    }
    private bool IsParkingAudio(EntityUid entity) => HasComp<AudioComponent>(entity)
        || MetaData(entity).EntityPrototype?.ID == "Audio";

    private bool IsSnapshotEntity(EntityUid entity) => !IsParkingAudio(entity)
        && MetaData(entity).EntityPrototype is not { MapSavable: false } && !HasComp<ShipShieldComponent>(entity);

    private bool IsInstalledEquipment(EntityUid entity, EntityUid grid)
    {
        var transform = Transform(entity);
        if (transform.ParentUid == grid && transform.Anchored &&
            TryComp<HardpointAnchorableOnlyComponent>(entity, out var weapon) && weapon.anchoredTo is { } mount &&
            TryComp<HardpointComponent>(mount, out var hardpoint) && hardpoint.anchoring == entity)
            return Transform(mount).GridUid == grid && Transform(mount).Anchored;

        // Machine boards and required stock material are prohibited as loose cargo. Keep them
        // installed, including the freshly spawned parts of a downgraded shield generator.
        var parent = transform.ParentUid;
        return (TryComp<MachineComponent>(parent, out var machine) &&
                (machine.BoardContainer.Contains(entity) || IsInstalledStockMaterial(entity, machine))) ||
               (HasComp<ComputerComponent>(parent) && _containers.TryGetContainer(parent, "board", out var board) && board.Contains(entity));
    }

    private bool IsInstalledStockMaterial(EntityUid entity, MachineComponent machine)
    {
        if (!machine.PartContainer.Contains(entity) || !TryComp<StackComponent>(entity, out var stack) ||
            stack.Unlimited || stack.Count <= 0 || Prototype(entity) is not { } prototype)
            return false;

        foreach (var board in machine.BoardContainer.ContainedEntities)
        {
            if (!TryComp<MachineBoardComponent>(board, out var requirements) ||
                !requirements.StackRequirements.TryGetValue(stack.StackTypeId, out var required) ||
                _prototypes.Index(stack.StackTypeId).Spawn.Id != prototype.ID)
                continue;

            // A parts container is not general cargo storage. Accept only the board's stock
            // stack prototype and required quantity; independently filter all descendants.
            var total = machine.PartContainer.ContainedEntities
                .Where(part => TryComp<StackComponent>(part, out var other) && other.StackTypeId == stack.StackTypeId)
                .Sum(part => (long) Comp<StackComponent>(part).Count);
            return total <= required;
        }
        return false;
    }

    private bool Prohibited(EntityUid entity, EntityUid grid)
    {
        if (HasNamed(entity, "Mech") || HasNamed(entity, "Store") || HasNamed(entity, "GhostRoleMobSpawner") ||
            HasNamed(entity, "OverwatchConsole")) return true;
        if (IsInstalledEquipment(entity, grid)) return false;
        if (HasNamed(entity, "Gun") || HasNamed(entity, "Contraband")) return true;
        if (!HasComp<ItemComponent>(entity)) return false;
        if (_prototypes.Index<EntityPrototype>("SafetyDepositBoxSmall").TryComp<StorageComponent>(out var safe, _components)
            && _whitelist.IsBlacklistPass(safe.Blacklist, entity)) return true;
        if (HasNamed(entity, "PressureProtection") || HasNamed(entity, "Implanter") || HasNamed(entity, "Uplink") || HasNamed(entity, "WeaponCaseInsertable")
            || HasNamed(entity, "Explosive") || HasNamed(entity, "ProjectileGrenade")) return true;
        // Generic tools also have MeleeWeapon; identify dedicated melee weapons by their prototype families.
        if (HasNamed(entity, "MeleeWeapon") && MetaData(entity).EntityPrototype is { } prototype
            && _prototypes.EnumerateAllParents<EntityPrototype>(prototype.ID, includeSelf: true)
                .Any(parent => MeleeWeaponRoots.Contains(parent.id) || parent.id.StartsWith("Weapon", StringComparison.Ordinal)
                    || parent.id.StartsWith("BaseWeapon", StringComparison.Ordinal))) return true;
        return false;
    }
    public List<EntityUid> ExcludedEntities(EntityUid grid)
    {
        var excluded = new HashSet<EntityUid>();
        foreach (var entity in Descendants(grid, entity => IsParkingAudio(entity) || IsParkingOccupant(entity)
                     || HasComp<GhostComponent>(entity) || HasComp<ShipShieldComponent>(entity)).Skip(1))
        {
            if (MetaData(entity).EntityPrototype is { MapSavable: false })
            {
                excluded.Add(entity);
                continue;
            }
            if (Original(entity, grid)) continue;
            var parent = Transform(entity).ParentUid;
            var depth = 0;
            var inherited = false;
            while (parent.IsValid() && parent != grid)
            {
                inherited |= excluded.Contains(parent);
                if (HasNamed(parent, "Storage") || HasNamed(parent, "EntityStorage")) depth++;
                parent = Transform(parent).ParentUid;
            }
            if (inherited || depth >= 2 || Prohibited(entity, grid)) excluded.Add(entity);
        }
        return excluded.ToList();
    }
    private string ReviewHash(EntityUid grid)
    {
        var excluded = ExcludedEntities(grid);
        return ParkingStore.Hash(GetCategory(grid) + ":" + string.Join('|', excluded.OrderBy(e => e.ToString()).Select(e => e + ":" + Name(e))));
    }

    private List<string> ExclusionPreview(EntityUid grid)
    {
        var excluded = ExcludedEntities(grid);
        var confiscated = ConfiscatedEntities(grid, excluded);
        var groups = excluded
            .GroupBy(entity => (Name: Name(entity), Confiscated: confiscated.Contains(entity)))
            .ToList();
        var preview = groups.Take(ExclusionPreviewLimit).Select(group =>
        {
            var (name, confiscation) = group.Key;
            var count = group.Count();
            var label = count > 1 ? $"{name} × {count}" : name;
            return confiscation ? $"{label} (конфискация)" : label;
        }).ToList();
        if (groups.Count > ExclusionPreviewLimit)
        {
            var remaining = groups.Skip(ExclusionPreviewLimit).ToList();
            var confiscatedCount = remaining.Where(group => group.Key.Confiscated).Sum(group => group.Count());
            var returnedCount = remaining.Sum(group => group.Count()) - confiscatedCount;
            preview.Add($"Остальные предметы: {confiscatedCount} к конфискации, {returnedCount} к выдаче у консоли.");
        }
        var sale = PreviewStorageSale(grid);
        preview.Add($"Содержимое хранилищ материалов будет продано за {sale.Materials:0.##} кредитов; технологии — за {sale.Research:0.##}.");
        preview.Add("Улучшения машин будут сброшены; щиты возвращены к исходному уровню или МС-100.");
        if (sale.UnpricedResearch > 0)
            preview.Add($"Знания без определённого эквивалента диска будут сброшены без выплаты: {sale.UnpricedResearch}.");
        return preview;
    }

    private void ValidateStorage(Context context, EntityUid grid, Guid slot)
    {
        CheckVoucherParkingAllowed(grid);
        ValidateParkingOrigin(context.Actor, Comp<ParkingShuttleComponent>(grid));
        CheckNotInFtl(grid);
        if (!Comp<ParkingShuttleComponent>(grid).Parked) CheckDocked(context.Console, grid);
        var target = _store.Slot(context.Session.UserId.UserId, slot);
        if (target.Ship != null || target.Phase != "empty" || GetCategory(grid) > target.Category) throw new InvalidOperationException("Нужно свободное место подходящей категории.");
        ParkingRules.RedemptionPrice(Comp<ParkingShuttleComponent>(grid).OriginalPrice);
    }
    private void PrepareReview(Context context, Guid slot)
    {
        var grid = OwnedShip(context) ?? throw new InvalidOperationException("На ID-карте нет вашего шаттла.");
        ValidateStorage(context, grid, slot);
        _reviews[context.Session.UserId.UserId] = new(Guid.NewGuid(), slot, grid, ReviewHash(grid), _timing.RealTime + TimeSpan.FromMinutes(2));
    }
    private sealed record SnapshotWrite(string Data, Dictionary<int, EntityUid> Written, HashSet<EntityUid> Omitted, Guid[]? RecoveryAssets = null);

    public string SerializeSnapshot(EntityUid grid) => SerializeSnapshot(grid, recover: false).Data;

    private SnapshotWrite SerializeSnapshot(EntityUid grid, bool recover) => SerializeSnapshotSteps(grid, recover, false).Last()!;

    private IEnumerable<SnapshotWrite?> SerializeSnapshotSteps(EntityUid grid, bool recover, bool backgroundEncode, bool idleParking = false)
    {
        CheckVoucherParkingAllowed(grid);
        _savePhase = "collect_entities";
        var collected = new List<EntityUid>();
        var writerTypes = new HashSet<Type>();
        foreach (var entity in Descendants(grid, IsParkingAudio))
        {
            if (idleParking && entity != grid && (HasComp<ActorComponent>(entity) ||
                TryComp<MindContainerComponent>(entity, out var mind) && mind.HasMind || HasComp<MapGridComponent>(entity)))
                throw new InvalidOperationException("На парковке обнаружен пассажир или связанный корпус; снимок отложен.");
            collected.Add(entity);
            if (idleParking)
                foreach (var component in AllComps(entity)) writerTypes.Add(component.GetType());
            if (idleParking && collected.Count % 16 == 0) yield return null;
        }
        var descendants = collected.ToArray();
#if DEEPLAGOON_ENGINE_INCREMENTAL_SNAPSHOTS
        if (idleParking)
        {
            var preparation = ((SerializationManager)_dependencies.Resolve<ISerializationManager>()).PrepareWritersAsync(writerTypes);
            _ = preparation.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            while (!preparation.IsCompleted) yield return null;
            preparation.GetAwaiter().GetResult();
        }
#endif
        if (descendants.Length > 100000) throw new InvalidOperationException("Слишком много объектов для сохранения шаттла.");
        var skipped = new HashSet<EntityUid>();
        var collectedCount = 0;
        foreach (var entity in descendants)
        {
            if (!IsSnapshotEntity(entity)) skipped.Add(entity);
            if (idleParking && ++collectedCount % 16 == 0) yield return null;
        }
        if (skipped.Count != 0 && !idleParking) DetachOriginalSurvivors(grid, skipped);
        var inside = new HashSet<EntityUid>();
        collectedCount = 0;
        foreach (var entity in descendants)
        {
            if (!skipped.Contains(entity) && !HasExcludedParent(entity, skipped, grid)) inside.Add(entity);
            if (idleParking && ++collectedCount % 16 == 0) yield return null;
        }
        HashSet<EntityUid> roots = [grid];
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(grid).MapID], FileCategory.Grid));
        var ship = Comp<ParkingShuttleComponent>(grid);
        var originals = ship.OriginalItems;
        var expeditionPolicy = ship.ExpeditionsEnabled;
        var expeditionCooldown = ship.ExpeditionCooldown;
        var expeditionNextOffer = ship.ExpeditionNextOffer;
        var omitted = new HashSet<EntityUid>();
        var repairReferences = new List<(ShipRepairEntitySpecifier Specifier, NetEntity Entity)>();
        EntitySerializer serializer;
        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= MaximumSnapshotAttempts) throw new InvalidDataException("Снимок не стабилизировался после четырёх попыток; исходный шаттл сохранён.");
            serializer = new EntitySerializer(_dependencies, new SerializationOptions
            {
                Category = FileCategory.Grid,
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
                EntityExceptionBehaviour = recover ? EntityExceptionBehaviour.IgnoreEntityAndChildren : EntityExceptionBehaviour.Rethrow,
            });
            serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= inside.Contains(entity.Owner);
            // Reserve all references first, then use the engine's single-entity API.
            // Never leave temporary component/link substitutions installed across a yield.
            var eligible = new HashSet<EntityUid>();
            foreach (var entity in Descendants(grid, entity => !serializer.IsSerializable(entity)))
            {
                if (serializer.IsSerializable(entity)) eligible.Add(entity);
                if (idleParking && eligible.Count % 16 == 0) yield return null;
            }
            if (idleParking)
            {
                foreach (var entity in eligible)
                {
                    serializer.ReserveYamlIds([entity]);
                    yield return null;
                }
            }
            else serializer.ReserveYamlIds(eligible);
#if DEEPLAGOON_ENGINE_INCREMENTAL_SNAPSHOTS
            if (idleParking)
            {
                foreach (var _ in serializer.PrepareGridComponentSteps(grid)) yield return null;
                foreach (var _ in serializer.PrepareComponentSteps(grid, "ParkingShuttle", context => PrepareFreeParkingShip(grid, ship, inside, context)))
                    yield return null;
                if (TryComp<ShipRepairDataComponent>(grid, out var blueprint))
                    foreach (var _ in serializer.PrepareComponentSteps(grid, "ShipRepairData", context => PrepareFreeParkingRepair(blueprint, inside, context)))
                        yield return null;
            }
#endif
            var ordered = eligible.ToArray();
            for (var start = 0; start < ordered.Length;)
            {
                _savePhase = "serialize_entities";
                var batch = new List<EntityUid>();
                // One complete entity can exceed the time target. Never split a component.
                do
                {
                    batch.Add(ordered[start++]);
                } while (start < ordered.Length && batch.Count < (idleParking ? 1 : SnapshotEntitiesPerStep));
                var processed = 0;
                var clock = Stopwatch.StartNew();
                try
                {
                    // Repair blueprints intentionally outlive destroyed pieces. Their optional live-entity
                    // reference must not force serialization of a deleted object or another grid.
                    foreach (var entity in batch)
                    {
                        if (idleParking) continue; // Chunked repair override has already filtered external references.
                        if (!TryComp<ShipRepairDataComponent>(entity, out var repair))
                            continue;
                        foreach (var specifier in repair.Chunks.Values.SelectMany(chunk => chunk.Entities.Values))
                        {
                            if (specifier.OriginalEntity is not { } net)
                                continue;
                            if (TryGetEntity(net, out var original) && original is { } uid && inside.Contains(uid))
                                continue;
                            repairReferences.Add((specifier, net));
                            specifier.OriginalEntity = null;
                        }
                    }
                    // Items removed from the ship, or deleted since purchase, are not part of this snapshot.
                    if (batch.Contains(grid) && !idleParking)
                    {
                        ship.OriginalItems = originals.Where(pair => inside.Contains(pair.Value)).ToDictionary();
                        ship.ResearchConnections = inside.Where(HasComp<ResearchClientComponent>).ToDictionary(entity => entity,
                            entity => _research.TryGetClientServer(entity, out var server, out _) && inside.Contains(server.Value) ? server : null);
                        var expeditions = _salvage.GridExpeditionSettings(grid);
                        ship.ExpeditionsEnabled = expeditions.Enabled;
                        ship.ExpeditionCooldown = expeditions.Cooldown;
                        ship.ExpeditionNextOffer = expeditions.NextOffer;
                    }
                    _deviceLinks.SerializeInternalLinks(inside, () =>
                    {
                        foreach (var entity in batch)
                        {
                            if (!serializer.ErroringEntities.Contains(entity)) serializer.SerializeEntity(entity);
                            processed++;
                            if (backgroundEncode && clock.Elapsed.TotalMilliseconds >= (idleParking ? .25 : SnapshotStepBudgetMilliseconds)) break;
                        }
                    }, batch);
                }
                finally
                {
                    foreach (var (specifier, reference) in repairReferences)
                        specifier.OriginalEntity = reference;
                    repairReferences.Clear();
                    ship.OriginalItems = originals;
                    ship.ResearchConnections = null;
                    ship.ExpeditionsEnabled = expeditionPolicy;
                    ship.ExpeditionCooldown = expeditionCooldown;
                    ship.ExpeditionNextOffer = expeditionNextOffer;
                }

                start -= batch.Count - processed;
                // The driver advances one batch per simulation update.
                yield return null;
            }

            if (serializer.ErroringEntities.Count == 0)
                break;
            if (serializer.ErroringEntities.Contains(grid))
                throw new InvalidDataException("Не удалось сохранить данные корпуса шаттла.");

            // Reserve IDs again with the failing subtrees excluded so references and container contents are consistent.
            omitted.UnionWith(serializer.ErroringEntities.Where(inside.Contains));
            inside.ExceptWith(serializer.ErroringEntities);
        }
        var recoveryAssets = new HashSet<Guid>();
        foreach (var entity in serializer.EntityData.Values)
        {
            if (idleParking && TryComp<Content.Server._DeepLagoon.CrashRecovery.RecoveryIdentityComponent>(entity.Uid, out var recovery))
                recoveryAssets.Add(recovery.Identity);
            if (!entity.Node.TryGet<SequenceDataNode>("components", out var components)) continue;
            foreach (var component in components.Cast<MappingDataNode>().ToArray())
            {
                var type = component.Get<ValueDataNode>("type").Value;
                if (TransientComponents.Contains(type))
                    components.Remove(component);
                else if (type == "ContainerContainer" && component.TryGet<MappingDataNode>("containers", out var containers))
                {
                    foreach (var container in containers.Values.Cast<MappingDataNode>())
                        if (container.TryGet<SequenceDataNode>("ents", out var contents))
                            foreach (var entry in contents.Cast<ValueDataNode>().Where(entry => entry.Value == "invalid").ToArray())
                                contents.Remove(entry);
                }
            }
            if (idleParking) yield return null;
        }
        MappingDataNode data;
        if (idleParking)
        {
            // Resolve live registries on the simulation thread, then assemble only
            // this serializer's owned, immutable entity nodes on the disk worker.
            var metadata = serializer.WriteMetadata();
            var tilemap = serializer.WriteTileMap();
            var maps = serializer.WriteIds(serializer.Maps);
            var grids = serializer.WriteIds(serializer.Grids);
            var orphans = serializer.WriteIds(serializer.Orphans);
            var nullspace = serializer.WriteIds(serializer.Nullspace);
            var assemble = Task.Run(() => new MappingDataNode
            {
                { "meta", metadata }, { "maps", maps }, { "grids", grids }, { "orphans", orphans },
                { "nullspace", nullspace }, { "tilemap", tilemap }, { "entities", serializer.WriteEntitySection() },
            });
            while (!assemble.IsCompleted) yield return null;
            data = assemble.GetAwaiter().GetResult();
        }
        else data = serializer.Write();
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));
        _savePhase = "encode_yaml";
        string Encode()
        {
            using var writer = new StringWriter();
            new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
            var value = writer.ToString();
            if (value.Length > 32_000_000) throw new InvalidOperationException("Снимок шаттла превышает допустимый размер.");
            return value;
        }
        string result;
        if (backgroundEncode)
        {
            var encoding = Task.Run(Encode);
            // Observe failures even if a round cancellation disposes this iterator.
            _ = encoding.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            while (!encoding.IsCompleted) yield return null;
            result = encoding.GetAwaiter().GetResult();
        }
        else result = Encode();
        yield return new(result, recover ? serializer.EntityData.ToDictionary(pair => pair.Key, pair => pair.Value.Uid) : [], omitted,
            idleParking ? recoveryAssets.ToArray() : null);
    }
    public EntityUid LoadSnapshot(string snapshot) => LoadSnapshot(snapshot, connectDevices: true);

    private EntityUid LoadSnapshot(string snapshot, bool connectDevices, bool trackEntities = false)
    {
        if (_staging == null || !Exists(_staging.Value))
        {
            _staging = _map.CreateMap(out var id);
            EnsureComp<ParkingStagingSnapshotComponent>(_staging.Value);
            EnsureComp<Content.Shared._DeepLagoon.Apartments.ApartmentIsolationComponent>(_staging.Value);
            _map.SetPaused(id, true);
        }
        using var reader = new StringReader(snapshot);
        if (!_loader.TryLoadGrid(Comp<MapComponent>(_staging.Value).MapId, reader, "stored shuttle", out var grid,
                new DeserializationOptions { LogInvalidEntities = false, StoreYamlUids = trackEntities }))
            throw new InvalidDataException("Не удалось загрузить снимок шаттла.");
        var restored = grid.Value.Owner;
        EnsureComp<ParkingStagingSnapshotComponent>(restored);
        try
        {
            RestoreSnapshotEquipment(restored, connectDevices);
            return restored;
        }
        catch
        {
            Del(restored);
            throw;
        }
    }

    private void RestoreSnapshotEquipment(EntityUid grid, bool connectDevices)
    {
        foreach (var _ in RestoreSnapshotEquipmentSteps(grid, connectDevices)) { }
    }

    private IEnumerable<bool> RestoreSnapshotEquipmentSteps(EntityUid grid, bool connectDevices)
    {
        if (connectDevices) RemoveRetiredEquipment(grid);
        var ship = Comp<ParkingShuttleComponent>(grid);
        _salvage.RestoreGridExpeditions(grid, ship.ExpeditionsEnabled,
            ship.ExpeditionsEnabled.HasValue ? ship.ExpeditionCooldown : null,
            ship.ExpeditionsEnabled.HasValue ? ship.ExpeditionNextOffer : null);
        var objects = new List<EntityUid>();
        foreach (var entity in Descendants(grid)) { objects.Add(entity); yield return true; }
        // Old snapshots could contain an active shield without its runtime references.
        RemComp<ShipShieldedComponent>(grid);
        foreach (var entity in objects)
        {
            yield return true;
            if (HasComp<ShipShieldComponent>(entity))
            {
                if (TryComp<ParkingOriginalComponent>(entity, out var original))
                    ship.OriginalItems.Remove(original.Identity);
                Del(entity);
                continue;
            }

            if (Transform(entity).Anchored &&
                TryComp<HardpointAnchorableOnlyComponent>(entity, out var weapon) && weapon.anchoredTo == null)
                _hardpoints.TryAnchorToHardpoint(entity, weapon);

            _upgradePower.RestoreUpgradeBases(entity);
            _construction.RestoreMachineParts(entity);

            if (TryComp<GunComponent>(entity, out var gun))
                EntityManager.System<SharedGunSystem>().RefreshModifiers((entity, gun));
            if (TryComp<MaterialReclaimerComponent>(entity, out var reclaimer))
                EntityManager.System<MaterialReclaimerSystem>().RestoreAfterLoad(entity, reclaimer);
            if (TryComp<AmeControllerComponent>(entity, out var ame))
                EntityManager.System<AmeControllerSystem>().RestoreAfterLoad(entity, ame);

            if (TryComp<FuelGeneratorComponent>(entity, out var generator))
                _generators.SetFuelGeneratorOn(entity, generator.On, generator);

            if (connectDevices && TryComp<DeviceNetworkComponent>(entity, out var device))
                _deviceNetwork.RestoreDeviceConnection(entity, device);
        }

        if (ship.ResearchConnections is { } connections)
        {
            foreach (var (client, server) in connections)
            {
                if (server is { } target && _transform.GetGrid(client) == grid && _transform.GetGrid(target) == grid)
                    _research.RegisterClient(client, target);
                yield return true;
            }
            ship.ResearchConnections = null;
        }
        else
        {
            // Legacy snapshots did not store the selected server. Only reconnect when the choice is unambiguous.
            var servers = objects.Where(HasComp<ResearchServerComponent>).Take(2).ToArray();
            if (servers.Length == 1)
                foreach (var client in objects.Where(HasComp<ResearchClientComponent>))
                {
                    _research.RegisterClient(client, servers[0]);
                    yield return true;
                }
        }

        foreach (var entity in objects)
        {
            yield return true;
            if (TerminatingOrDeleted(entity))
                continue;
            if (HasComp<MaterialStorageComponent>(entity))
            {
                _appearance.SetData(entity, MaterialStorageVisuals.Inserting, HasComp<InsertingMaterialStorageComponent>(entity));
            }
            if (TryComp<LatheComponent>(entity, out var lathe))
                EntityManager.System<LatheSystem>().RestoreAfterLoad(entity, lathe);
        }
        if (HasComp<Content.Server._Mono.FireControl.FireControlGridComponent>(grid))
            EntityManager.System<FireControlSystem>().RefreshControllables(grid);
    }
    private bool HasExcludedParent(EntityUid entity, HashSet<EntityUid> excluded, EntityUid grid)
    {
        for (var parent = Transform(entity).ParentUid; parent.IsValid() && parent != grid; parent = Transform(parent).ParentUid)
            if (excluded.Contains(parent)) return true;
        return false;
    }

    private HashSet<EntityUid> ConfiscatedEntities(EntityUid grid, IEnumerable<EntityUid> excluded)
    {
        var prohibited = excluded.Where(entity => Prohibited(entity, grid)).ToHashSet();
        var procurement = EntityManager.System<Content.Server._DeepLagoon.Procurement.ProcurementSystem>();
        return excluded.Where(entity => !procurement.ProtectsCrypto(entity) &&
            (prohibited.Contains(entity) || HasExcludedParent(entity, prohibited, grid))).ToHashSet();
    }
    private void DetachOriginalSurvivors(EntityUid grid, HashSet<EntityUid> excluded)
    {
        foreach (var entity in Descendants(grid).ToArray())
        {
            if (!Original(entity, grid) || !HasExcludedParent(entity, excluded, grid)) continue;
            var position = Vector2.Transform(_transform.GetWorldPosition(entity), _transform.GetInvWorldMatrix(grid));
            var rotation = _transform.GetWorldRotation(entity) - _transform.GetWorldRotation(grid);
            _containers.TryRemoveFromContainer(entity);
            _transform.SetCoordinates(entity, new EntityCoordinates(grid, position));
            _transform.SetLocalRotation(entity, rotation);
        }
    }

    private void ReportOriginalEquipmentMismatch(EntityUid source, EntityUid restored)
    {
        var expected = Descendants(source, IsParkingAudio)
            .Where(entity => IsSnapshotEntity(entity) && Original(entity, source))
            .ToDictionary(entity => Comp<ParkingOriginalComponent>(entity).Identity);
        var actual = Descendants(restored)
            .Where(entity => Original(entity, restored))
            .Select(entity => Comp<ParkingOriginalComponent>(entity).Identity)
            .ToHashSet();
        if (actual.SetEquals(expected.Keys))
            return;

        var missing = expected.Where(pair => !actual.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        var examples = string.Join(", ", missing.Take(12).Select(entity => ToPrettyString(entity).ToString()));
        var unexpected = actual.Count - (expected.Count - missing.Length);
        Log.Warning($"Не всё штатное оборудование прошло проверку загрузки. " +
            $"Грид {ToPrettyString(source)}. Исходных: {expected.Count}, восстановлено: {actual.Count}, " +
            $"пропало: {missing.Length}, лишних: {unexpected}. Пропавшее оборудование: {examples}");
    }

    private Dictionary<EntityUid, EntityUid> MatchSnapshotEntities(SnapshotWrite snapshot, EntityUid restored)
    {
        var matches = new Dictionary<EntityUid, EntityUid>();
        foreach (var entity in Descendants(restored))
        {
            if (TryComp<YamlUidComponent>(entity, out var id) && snapshot.Written.TryGetValue(id.Uid, out var original))
                matches.Add(entity, original);
        }
        return matches;
    }

    private EntityUid[] ReportSnapshotOmissions(EntityUid source, SnapshotWrite snapshot, Dictionary<EntityUid, EntityUid> matches)
    {
        var loaded = matches.Values.ToHashSet();
        var missing = snapshot.Omitted.Concat(snapshot.Written.Values.Where(entity => !loaded.Contains(entity))).ToArray();
        if (missing.Length != 0)
        {
            var examples = string.Join(", ", missing.Take(12).Select(entity => ToPrettyString(entity).ToString()));
            Log.Warning($"Снимок шаттла {ToPrettyString(source)} восстановлен без {missing.Length} объектов: {examples}");
        }
        return missing;
    }

    public string FilteredSnapshot(EntityUid grid) => FilteredSnapshot(grid, out _, out _);

    private string FilteredSnapshot(EntityUid grid, out HashSet<EntityUid> omitted, out HashSet<EntityUid> retained)
    {
        var result = FilteredSnapshotSteps(grid, false).Last()!;
        omitted = result.Omitted;
        retained = result.Retained;
        return result.Data;
    }

    private sealed record FilteredSnapshotResult(string Data, HashSet<EntityUid> Omitted, HashSet<EntityUid> Retained);

    private IEnumerable<FilteredSnapshotResult?> FilteredSnapshotSteps(EntityUid grid, bool backgroundEncode)
    {
        SnapshotWrite initial = default!;
        foreach (var step in SerializeSnapshotSteps(grid, true, backgroundEncode))
        {
            if (step == null) yield return null;
            else initial = step;
        }
        _savePhase = "load_clone";
        var clone = LoadSnapshot(initial.Data, connectDevices: false, trackEntities: true);
        try
        {
            yield return null;
            _savePhase = "filter_clone";
            var origins = MatchSnapshotEntities(initial, clone);
            var omitted = ReportSnapshotOmissions(grid, initial, origins).ToHashSet();
            var excluded = ExcludedEntities(clone).ToHashSet();
            DetachOriginalSurvivors(clone, excluded);
            foreach (var entity in excluded.Where(e => !HasExcludedParent(e, excluded, clone)).ToArray()) Del(entity);
            NormalizeStoredEquipment(clone, origins);
            Comp<ParkingShuttleComponent>(clone).Parked = false;
            for (var attempt = 0; attempt < MaximumSnapshotAttempts; attempt++)
            {
                yield return null;
                SnapshotWrite snapshot = default!;
                foreach (var step in SerializeSnapshotSteps(clone, true, backgroundEncode))
                {
                    if (step == null) yield return null;
                    else snapshot = step;
                }
                _savePhase = "validate_load";
                // Validate the actual load path before committing and removing anything from the live ship.
                var check = LoadSnapshot(snapshot.Data, connectDevices: false, trackEntities: true);
                try
                {
                    yield return null;
                    _savePhase = "validate_snapshot";
                    if (GetCategory(check) != GetCategory(grid))
                        throw new InvalidDataException("Снимок не прошёл проверку категории шаттла.");
                    var unexpected = ExcludedEntities(check);
                    if (unexpected.Count != 0)
                        throw new InvalidDataException("Снимок содержит недопустимые предметы: " +
                            string.Join(", ", unexpected.Take(8).Select(entity => Prototype(entity)?.ID ?? Name(entity))) + ".");
                    if (!_map.GetAllTiles(grid, Comp<MapGridComponent>(grid)).Select(tile => (tile.GridIndices, tile.Tile)).ToHashSet()
                        .SetEquals(_map.GetAllTiles(check, Comp<MapGridComponent>(check)).Select(tile => (tile.GridIndices, tile.Tile))))
                        throw new InvalidDataException("Геометрия шаттла не прошла проверку загрузки.");
                    if (_docking.GetDocks(grid).Count != 0 && _docking.GetDocks(check).Count == 0)
                        throw new InvalidDataException("После загрузки шаттл потерял все стыковочные порты и его нельзя вернуть с парковки.");

                    var matches = MatchSnapshotEntities(snapshot, check);
                    var lostDuringLoad = false;
                    foreach (var missing in ReportSnapshotOmissions(grid, snapshot, matches))
                    {
                        if (!origins.TryGetValue(missing, out var original))
                            continue;
                        omitted.Add(original);
                        lostDuringLoad |= !snapshot.Omitted.Contains(missing);
                    }

                    origins = matches.Where(pair => origins.ContainsKey(pair.Value))
                        .ToDictionary(pair => pair.Key, pair => origins[pair.Value]);
                    if (!lostDuringLoad)
                    {
                        var retained = origins.Values.ToHashSet();
                        ReportOriginalEquipmentMismatch(grid, check);
                        yield return new(snapshot.Data, omitted, retained);
                        yield break;
                    }

                    // Re-save the loaded copy so items returned at the console cannot also reappear on redemption.
                    (clone, check) = (check, clone);
                }
                finally { if (Exists(check)) Del(check); }
            }
            throw new InvalidDataException("Проверка снимка не стабилизировалась после четырёх попыток; исходный шаттл сохранён.");
        }
        finally { if (Exists(clone)) Del(clone); }
    }
    private Task<string> Save(Context context, Guid slot, Guid token)
    {
        if (_saveWork != null) throw new InvalidOperationException("Дождитесь завершения текущего сохранения шаттла.");
        var user = context.Session.UserId.UserId;
        if (!_reviews.TryGetValue(user, out var review) || review.Token != token || review.Slot != slot || review.Expires < _timing.RealTime
            || OwnedShip(context) != review.Grid) throw new InvalidOperationException("Сначала запросите проверку сохранения ещё раз.");
        var grid = review.Grid;
        ValidateStorage(context, grid, slot);
        if (ReviewHash(grid) != review.Hash) { _reviews.Remove(user); throw new InvalidOperationException("Состав исключаемых предметов или размер изменился. Проверьте список заново."); }
        // Undock before serialization so joints and docking references to the live station cannot enter the file.
        // A failed save leaves the intact source ship safely parked and freely recallable.
        if (!Comp<ParkingShuttleComponent>(grid).Parked)
            Park(context.Console, grid, context.Character);
        else
        {
            // A ship parked before this release can still have NPCs aboard.
            EvacuateParkingOccupants(grid, NearbyStationDocks(context.Console));
            ReleaseGhosts(Descendants(grid).Where(HasComp<GhostComponent>).ToList());
        }
        var work = new SaveWork(context, slot, grid, FilteredSnapshotSteps(grid, true).GetEnumerator());
        _saveWork = work;
        return work.Completion.Task;
    }

    private SavePlan PrepareSavePlan(SaveWork work, FilteredSnapshotResult snapshot)
    {
        var context = work.Context;
        var grid = work.Grid;
        var user = context.Session.UserId.UserId;
        var ship = Comp<ParkingShuttleComponent>(grid);
        var excluded = ExcludedEntities(grid).ToHashSet();
        var retained = snapshot.Retained;
        var confiscated = ConfiscatedEntities(grid, excluded);
        confiscated.ExceptWith(retained);
        var researchSale = QuoteResearchSale(retained);
        var materialValue = StoredMaterialValue(retained);
        var confiscatedValue = confiscated.Where(IsSnapshotEntity)
            .Sum(entity => _pricing.GetPriceWithVendingDiscount(entity, grid, includeContents: false));
        var price = (int) Math.Clamp(confiscatedValue + materialValue + researchSale.Price, 0, int.MaxValue);
        var payout = price > 0 ? new ParkingPayout(Guid.NewGuid(), user, context.Character, price) : null;
        var stored = new StoredShuttle(ship.Identity, Name(grid), ship.OwnerName, GetCategory(grid), ship.OriginalPrice,
            snapshot.Data, "", SavedShift: _shift, PurchasedWithVoucher: ship.PurchasedWithVoucher,
            OriginFaction: ship.OriginFaction, OwnerProfile: ship.ParkingProfile, Vessel: ship.Vessel);
        excluded.UnionWith(snapshot.Omitted);
        var researchNotice = researchSale.Unsupported > 0
            ? $" Сброшены без выплаты знания без эквивалента диска: {researchSale.Unsupported}." : "";
        if (researchSale.Recipes.Count > 0 && researchSale.Price == 0)
            researchNotice += " Штатная стоимость проданных техдисков равна нулю.";
        return new(stored, payout, retained, excluded, confiscated, confiscatedValue, materialValue,
            researchSale.Price, researchSale.Unsupported, researchNotice);
    }

    private string FinishSavedShip(SaveWork work, SavePlan plan)
    {
        var context = work.Context;
        var grid = work.Grid;
        var user = context.Session.UserId.UserId;
        var ship = Comp<ParkingShuttleComponent>(grid);
        ForgetFreeParkingAfterPaidSave(ship.Identity);
        if (plan.Payout is { } payout)
            EntityManager.System<Content.Server._DeepLagoon.Procurement.ProcurementSystem>()
                .CaptureParkingManifest(payout.Id, user, context.Character, payout.Amount, plan.ConfiscatedValue,
                    plan.MaterialValue, plan.ResearchPrice, plan.UnsupportedResearch, plan.Retained);
        EntityManager.System<Content.Server._DeepLagoon.CrashRecovery.CrashRecoverySystem>().ResearchPersisted(plan.Retained);
        EntityManager.System<Content.Server._DeepLagoon.CrashRecovery.CrashRecoverySystem>().Persisted(plan.Retained);
        ReturnParkingItems(grid, context.Console, plan.Excluded, plan.Confiscated, plan.Retained);
        RemoveParkedShip(grid, ship);
        _reviews.Remove(user);
        return plan.Payout == null
            ? "Шаттл сохранён. Разрешённые исключённые предметы оставлены у консоли." + plan.ResearchNotice
            : $"Шаттл сохранён. Выплата за конфискат, материалы и технологии: {plan.Payout.Amount} кредитов; зачисление будет проверено автоматически." + plan.ResearchNotice;
    }

    private void ReturnParkingItems(EntityUid grid, EntityUid console, HashSet<EntityUid> excluded,
        HashSet<EntityUid> confiscated, HashSet<EntityUid> retained)
    {
        var entities = Descendants(grid).Skip(1).ToArray();
        var returned = new HashSet<EntityUid>();
        foreach (var entity in entities)
        {
            if (!retained.Contains(entity) && !confiscated.Contains(entity)
                && (excluded.Contains(entity) || returned.Contains(Transform(entity).ParentUid)))
                returned.Add(entity);
        }

        // The original suit can otherwise delete its former hood when the ship is removed.
        foreach (var entity in returned)
        {
            if (TryComp<AttachedClothingComponent>(entity, out var attached) && !returned.Contains(attached.AttachedUid))
                RemComp<AttachedClothingComponent>(entity);
        }

        // Split the contents by destination before moving or deleting their containers.
        foreach (var entity in entities.Reverse())
        {
            if (TerminatingOrDeleted(entity))
                continue;

            var parentReturned = returned.Contains(Transform(entity).ParentUid);
            if (confiscated.Contains(entity))
                Del(entity);
            else if (returned.Contains(entity) && !parentReturned)
            {
                _containers.TryRemoveFromContainer(entity, force: true);
                _transform.DropNextTo(entity, console);
            }
            else if (parentReturned && retained.Contains(entity))
            {
                _containers.TryRemoveFromContainer(entity, force: true);
                _transform.SetCoordinates(entity, new EntityCoordinates(grid, Vector2.Zero));
            }
        }
    }

    private void RemoveParkedShip(EntityUid grid, ParkingShuttleComponent ship)
    {
        var deeds = AllEntityQuery<Content.Shared._NF.Shipyard.Components.ShuttleDeedComponent>();
        var cards = new List<EntityUid>();
        while (deeds.MoveNext(out var entity, out var deed)) if (entity != grid && deed.ShuttleUid == grid) cards.Add(entity);
        foreach (var card in cards) RemComp<Content.Shared._NF.Shipyard.Components.ShuttleDeedComponent>(card);
        // Split-off grids are not part of this single-grid snapshot and must not be deleted with it.
        RemComp<Content.Server.StationEvents.Components.LinkedLifecycleGridParentComponent>(grid);
        Del(grid);
        if (ship.ReturnStation is { } former && Exists(former) && TryComp<Content.Shared.Station.Components.StationDataComponent>(former, out var formerData) && formerData.Grids.Count == 0)
            _stations.DeleteStation(former, formerData);
        if (_parkingMaps.Remove(grid, out var parking) && Exists(parking)) Del(parking);
    }
}
