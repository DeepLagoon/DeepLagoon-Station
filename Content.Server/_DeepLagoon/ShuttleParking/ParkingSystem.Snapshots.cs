using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._Crescent.Hardpoint;
using Content.Server._NF.Power.EntitySystems;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Power.Generator;
using Content.Server.Research.Systems;
using Content.Shared._Crescent.Hardpoints;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Item;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Storage;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
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
    private bool IsSnapshotEntity(EntityUid entity) =>
        MetaData(entity).EntityPrototype is not { MapSavable: false } && !HasComp<ShipShieldComponent>(entity);

    private bool IsInstalledEquipment(EntityUid entity, EntityUid grid)
    {
        var transform = Transform(entity);
        if (transform.ParentUid == grid && transform.Anchored &&
            TryComp<HardpointAnchorableOnlyComponent>(entity, out var weapon) && weapon.anchoredTo is { } mount &&
            TryComp<HardpointComponent>(mount, out var hardpoint) && hardpoint.anchoring == entity)
            return Transform(mount).GridUid == grid && Transform(mount).Anchored;

        // Machine boards are prohibited as loose cargo. Keep boards installed in their own devices.
        var parent = transform.ParentUid;
        return (TryComp<MachineComponent>(parent, out var machine) && machine.BoardContainer.Contains(entity)) ||
               (HasComp<ComputerComponent>(parent) && _containers.TryGetContainer(parent, "board", out var board) && board.Contains(entity));
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
        foreach (var entity in Descendants(grid).Skip(1))
        {
            if (HasComp<ShipShieldComponent>(entity))
                continue;
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
        return preview;
    }

    private void ValidateStorage(Context context, EntityUid grid, Guid slot)
    {
        CheckEmpty(grid);
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
    public string SerializeSnapshot(EntityUid grid)
    {
        var descendants = Descendants(grid).ToArray();
        if (descendants.Length > 100000) throw new InvalidOperationException("Слишком много объектов для сохранения шаттла.");
        var skipped = descendants.Where(entity => !IsSnapshotEntity(entity)).ToHashSet();
        if (skipped.Count != 0) DetachOriginalSurvivors(grid, skipped);
        var inside = descendants.Where(entity => !skipped.Contains(entity) && !HasExcludedParent(entity, skipped, grid)).ToHashSet();
        HashSet<EntityUid> roots = [grid];
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(grid).MapID], FileCategory.Grid));
        var serializer = new EntitySerializer(_dependencies, new SerializationOptions
        {
            Category = FileCategory.Grid,
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
            ErrorOnOrphan = false,
        });
        serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= inside.Contains(entity.Owner);
        var ship = Comp<ParkingShuttleComponent>(grid);
        var originals = ship.OriginalItems;
        try
        {
            // Items removed from the ship, or deleted since purchase, are not part of this snapshot.
            ship.OriginalItems = originals.Where(pair => inside.Contains(pair.Value)).ToDictionary();
            ship.ResearchConnections = inside.Where(HasComp<ResearchClientComponent>).ToDictionary(entity => entity,
                entity => _research.TryGetClientServer(entity, out var server, out _) && inside.Contains(server.Value) ? server : null);
            _deviceLinks.SerializeInternalLinks(inside, () => serializer.SerializeEntityRecursive(roots));
        }
        finally
        {
            ship.OriginalItems = originals;
            ship.ResearchConnections = null;
        }
        foreach (var entity in serializer.EntityData.Values)
        {
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
        }
        var data = serializer.Write();
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        var result = writer.ToString();
        if (result.Length > 32_000_000) throw new InvalidOperationException("Снимок шаттла превышает допустимый размер.");
        return result;
    }
    public EntityUid LoadSnapshot(string snapshot) => LoadSnapshot(snapshot, connectDevices: true);

    private EntityUid LoadSnapshot(string snapshot, bool connectDevices)
    {
        if (_staging == null || !Exists(_staging.Value))
        {
            _staging = _map.CreateMap(out var id);
            EnsureComp<Content.Shared._DeepLagoon.Apartments.ApartmentIsolationComponent>(_staging.Value);
            _map.SetPaused(id, true);
        }
        using var reader = new StringReader(snapshot);
        if (!_loader.TryLoadGrid(Comp<MapComponent>(_staging.Value).MapId, reader, "stored shuttle", out var grid,
                new DeserializationOptions { LogInvalidEntities = false }))
            throw new InvalidDataException("Не удалось загрузить снимок шаттла.");
        var restored = grid.Value.Owner;
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
        var ship = Comp<ParkingShuttleComponent>(grid);
        var objects = Descendants(grid).ToArray();
        // Old snapshots could contain an active shield without its runtime references.
        RemComp<ShipShieldedComponent>(grid);
        foreach (var entity in objects)
        {
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

            if (TryComp<FuelGeneratorComponent>(entity, out var generator))
                _generators.SetFuelGeneratorOn(entity, generator.On, generator);

            if (connectDevices && TryComp<DeviceNetworkComponent>(entity, out var device))
                _deviceNetwork.RestoreDeviceConnection(entity, device);
        }

        if (ship.ResearchConnections is { } connections)
        {
            foreach (var (client, server) in connections)
                if (server is { } target && _transform.GetGrid(client) == grid && _transform.GetGrid(target) == grid)
                    _research.RegisterClient(client, target);
            ship.ResearchConnections = null;
        }
        else
        {
            // Legacy snapshots did not store the selected server. Only reconnect when the choice is unambiguous.
            var servers = objects.Where(HasComp<ResearchServerComponent>).Take(2).ToArray();
            if (servers.Length == 1)
                foreach (var client in objects.Where(HasComp<ResearchClientComponent>))
                    _research.RegisterClient(client, servers[0]);
        }
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
        return excluded.Where(entity => prohibited.Contains(entity) || HasExcludedParent(entity, prohibited, grid)).ToHashSet();
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
    public string FilteredSnapshot(EntityUid grid)
    {
        var clone = LoadSnapshot(SerializeSnapshot(grid), connectDevices: false);
        try
        {
            var excluded = ExcludedEntities(clone).ToHashSet();
            DetachOriginalSurvivors(clone, excluded);
            foreach (var entity in excluded.Where(e => !HasExcludedParent(e, excluded, clone)).ToArray()) Del(entity);
            Comp<ParkingShuttleComponent>(clone).Parked = false;
            var snapshot = SerializeSnapshot(clone);
            // Validate the actual load path before committing and removing anything from the live ship.
            var check = LoadSnapshot(snapshot, connectDevices: false);
            try
            {
                if (GetCategory(check) != GetCategory(grid) || ExcludedEntities(check).Count != 0) throw new InvalidDataException("Снимок не прошёл проверку загрузки.");
                if (Descendants(check).Count(entity => Original(entity, check)) !=
                    Descendants(grid).Count(entity => IsSnapshotEntity(entity) && Original(entity, grid)))
                    throw new InvalidDataException("Не всё штатное оборудование прошло проверку загрузки.");
                if (!_map.GetAllTiles(grid, Comp<Robust.Shared.Map.Components.MapGridComponent>(grid)).Select(tile => (tile.GridIndices, tile.Tile)).ToHashSet()
                    .SetEquals(_map.GetAllTiles(check, Comp<Robust.Shared.Map.Components.MapGridComponent>(check)).Select(tile => (tile.GridIndices, tile.Tile))))
                    throw new InvalidDataException("Геометрия шаттла не прошла проверку загрузки.");
            }
            finally { Del(check); }
            return snapshot;
        }
        finally { Del(clone); }
    }
    private string Save(Context context, Guid slot, Guid token)
    {
        var user = context.Session.UserId.UserId;
        if (!_reviews.TryGetValue(user, out var review) || review.Token != token || review.Slot != slot || review.Expires < _timing.RealTime
            || OwnedShip(context) != review.Grid) throw new InvalidOperationException("Сначала запросите проверку сохранения ещё раз.");
        var grid = review.Grid;
        ValidateStorage(context, grid, slot);
        if (ReviewHash(grid) != review.Hash) { _reviews.Remove(user); throw new InvalidOperationException("Состав исключаемых предметов или размер изменился. Проверьте список заново."); }
        // Undock before serialization so joints and docking references to the live station cannot enter the file.
        // A failed save leaves the intact source ship safely parked and freely recallable.
        if (!Comp<ParkingShuttleComponent>(grid).Parked) Park(context.Console, grid, context.Character);
        var snapshot = FilteredSnapshot(grid);
        var ship = Comp<ParkingShuttleComponent>(grid);
        var excluded = ExcludedEntities(grid).ToHashSet();
        var confiscated = ConfiscatedEntities(grid, excluded);
        var price = (int) Math.Clamp(confiscated
            .Where(IsSnapshotEntity)
            .Sum(entity => _pricing.GetPriceWithVendingDiscount(entity, grid, includeContents: false)), 0, int.MaxValue);
        var payout = price > 0 ? new ParkingPayout(Guid.NewGuid(), user, context.Character, price) : null;
        // FULL synchronous SQLite commit precedes all irreversible world mutations.
        _store.Save(user, slot, new(ship.Identity, Name(grid), ship.OwnerName, GetCategory(grid), ship.OriginalPrice, snapshot, ParkingStore.Hash(snapshot)), payout);
        DetachOriginalSurvivors(grid, excluded);
        // Remove contents before their containers can eject them during deletion.
        foreach (var entity in Descendants(grid).Where(confiscated.Contains).Reverse().ToArray())
            Del(entity);
        excluded.ExceptWith(confiscated);
        foreach (var entity in excluded.Where(e => !HasExcludedParent(e, excluded, grid)).ToArray())
        {
            _containers.TryRemoveFromContainer(entity);
            _transform.DropNextTo(entity, context.Console);
        }

        RemoveParkedShip(grid, ship);
        _store.FinishSave(user, slot);
        _reviews.Remove(user);
        if (payout == null)
            return "Шаттл сохранён. Разрешённые исключённые предметы оставлены у консоли.";

        return PaySale(payout)
            ? $"Шаттл сохранён. За конфискат зачислено {price} кредитов. Остальные исключённые предметы оставлены у консоли."
            : "Шаттл сохранён. Выплата за конфискат ожидает зачисления.";
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
