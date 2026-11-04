using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Item;
using Content.Shared.Storage;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
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
    [Dependency] private IDependencyCollection _dependencies = default!;
    [Dependency] private IComponentFactory _components = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    private static readonly HashSet<string> ExternalComponents = ["ShuttleDeed", "ShipGuestAccess", "ShipOwnership", "StationMember", "LinkedLifecycleGridParent", "LinkedLifecycleGridChild"];
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
    private bool Prohibited(EntityUid entity)
    {
        if (HasNamed(entity, "Mech") || HasNamed(entity, "Gun") || HasNamed(entity, "Contraband")
            || HasNamed(entity, "Store") || HasNamed(entity, "GhostRoleMobSpawner") || HasNamed(entity, "OverwatchConsole")) return true;
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
            if (inherited || depth >= 2 || Prohibited(entity)) excluded.Add(entity);
        }
        return excluded.ToList();
    }
    private string ReviewHash(EntityUid grid)
    {
        var excluded = ExcludedEntities(grid);
        if (excluded.Count > 500) throw new InvalidOperationException("Слишком много исключаемых предметов. Сначала разгрузите шаттл.");
        return ParkingStore.Hash(GetCategory(grid) + ":" + string.Join('|', excluded.OrderBy(e => e.ToString()).Select(e => e + ":" + Name(e))));
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
        var inside = Descendants(grid).ToHashSet();
        if (inside.Count > 100000) throw new InvalidOperationException("Слишком много объектов для сохранения шаттла.");
        var skipped = new HashSet<EntityUid>();
        foreach (var entity in Descendants(grid).Skip(1))
            if (MetaData(entity).EntityPrototype is { MapSavable: false }) skipped.Add(entity);
        if (skipped.Count != 0) DetachOriginalSurvivors(grid, skipped);
        HashSet<EntityUid> roots = [grid];
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(grid).MapID], FileCategory.Grid));
        var serializer = new EntitySerializer(_dependencies, new SerializationOptions { Category = FileCategory.Grid, ExpectPreInit = false, MissingEntityBehaviour = MissingEntityBehaviour.IncludeNullspace });
        // External runtime references are intentionally severed, never pulled into a ship save.
        var outside = AllEntityQuery<TransformComponent>();
        while (outside.MoveNext(out var entity, out _))
            if (!inside.Contains(entity)) serializer.ErroringEntities.Add(entity);
        serializer.OnIsSerializeable += (Entity<MetaDataComponent> entity, ref bool serializable) => serializable &= inside.Contains(entity.Owner);
        var ship = Comp<ParkingShuttleComponent>(grid);
        var originals = ship.OriginalItems;
        // Items removed from the ship, or deleted since purchase, are not part of this snapshot.
        ship.OriginalItems = originals.Where(pair => inside.Contains(pair.Value) && Exists(pair.Value)).ToDictionary();
        try { serializer.SerializeEntityRecursive(roots); }
        finally { ship.OriginalItems = originals; }
        var data = serializer.Write();
        foreach (var entity in serializer.EntityData.Values)
        {
            if (!entity.Node.TryGet<SequenceDataNode>("components", out var components)) continue;
            foreach (var component in components.Cast<MappingDataNode>().ToArray())
                if (ExternalComponents.Contains(component.Get<ValueDataNode>("type").Value)) components.Remove(component);
        }
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, FileCategory.Grid));
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        var result = writer.ToString();
        if (result.Length > 32_000_000) throw new InvalidOperationException("Снимок шаттла превышает допустимый размер.");
        return result;
    }
    public EntityUid LoadSnapshot(string snapshot)
    {
        if (_staging == null || !Exists(_staging.Value))
        {
            _staging = _map.CreateMap(out var id);
            EnsureComp<Content.Shared._DeepLagoon.Apartments.ApartmentIsolationComponent>(_staging.Value);
            _map.SetPaused(id, true);
        }
        using var reader = new StringReader(snapshot);
        if (!_loader.TryLoadGrid(Comp<Robust.Shared.Map.Components.MapComponent>(_staging.Value).MapId, reader, "stored shuttle", out var grid))
            throw new InvalidDataException("Не удалось загрузить снимок шаттла.");
        return grid.Value.Owner;
    }
    private bool HasExcludedParent(EntityUid entity, HashSet<EntityUid> excluded, EntityUid grid)
    {
        for (var parent = Transform(entity).ParentUid; parent.IsValid() && parent != grid; parent = Transform(parent).ParentUid)
            if (excluded.Contains(parent)) return true;
        return false;
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
        var clone = LoadSnapshot(SerializeSnapshot(grid));
        try
        {
            var excluded = ExcludedEntities(clone).ToHashSet();
            DetachOriginalSurvivors(clone, excluded);
            foreach (var entity in excluded.Where(e => !HasExcludedParent(e, excluded, clone)).ToArray()) Del(entity);
            Comp<ParkingShuttleComponent>(clone).Parked = false;
            var snapshot = SerializeSnapshot(clone);
            // Validate the actual load path before committing and removing anything from the live ship.
            var check = LoadSnapshot(snapshot);
            try
            {
                if (GetCategory(check) != GetCategory(grid) || ExcludedEntities(check).Count != 0) throw new InvalidDataException("Снимок не прошёл проверку загрузки.");
                if (Descendants(check).Count(entity => Original(entity, check)) != Descendants(grid).Count(entity => Original(entity, grid)))
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
    private void Save(Context context, Guid slot, Guid token)
    {
        var user = context.Session.UserId.UserId;
        if (!_reviews.TryGetValue(user, out var review) || review.Token != token || review.Slot != slot || review.Expires < _timing.RealTime
            || OwnedShip(context) != review.Grid) throw new InvalidOperationException("Сначала запросите проверку сохранения ещё раз.");
        var grid = review.Grid;
        ValidateStorage(context, grid, slot);
        if (ReviewHash(grid) != review.Hash) { _reviews.Remove(user); throw new InvalidOperationException("Состав исключаемых предметов или размер изменился. Проверьте список заново."); }
        // Undock before serialization so joints and docking references to the live station cannot enter the file.
        // A failed save leaves the intact source ship safely parked and freely recallable.
        if (!Comp<ParkingShuttleComponent>(grid).Parked) Park(context.Console, grid);
        var snapshot = FilteredSnapshot(grid);
        var ship = Comp<ParkingShuttleComponent>(grid);
        var excluded = ExcludedEntities(grid).ToHashSet();
        // FULL synchronous SQLite commit precedes all irreversible world mutations.
        _store.Save(user, slot, new(ship.Identity, Name(grid), ship.OwnerName, GetCategory(grid), ship.OriginalPrice, snapshot, ParkingStore.Hash(snapshot)));
        DetachOriginalSurvivors(grid, excluded);
        foreach (var entity in excluded.Where(e => !HasExcludedParent(e, excluded, grid)).ToArray())
        {
            _containers.TryRemoveFromContainer(entity);
            _transform.DropNextTo(entity, context.Console);
        }
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
        _store.FinishSave(user, slot); _reviews.Remove(user);
    }
}
