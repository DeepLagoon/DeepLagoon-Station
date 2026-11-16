using System.Linq;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Storage;
using Robust.Shared.Containers;

namespace Content.Shared.Station;

public abstract partial class SharedStationSpawningSystem
{
    [Dependency] private SharedContainerSystem _containers = default!;

    public static IEnumerable<StartingGearStorageItem> GetStartingGearStorageItems(
        IEquipmentLoadout gear,
        PersonalLoadoutCustomization? customization = null)
    {
        var source = gear switch
        {
            LoadoutPrototype loadout => "loadout/" + loadout.ID,
            StartingGearPrototype startingGear => "gear/" + startingGear.ID,
            _ => null,
        };
        foreach (var (slot, items) in gear.Storage)
        {
            if (items == null)
                continue;
            for (var i = 0; i < items.Count; i++)
                yield return new StartingGearStorageItem(slot, items[i], customization,
                    slot == "back" && source != null ? $"{source}/{i}/{items[i].Id}" : null);
        }
    }

    /// <summary>Find the storage available through the equipped backpack or MODsuit.</summary>
    public Entity<StorageComponent>? GetBackpackStorage(EntityUid entity, bool preview = false)
    {
        if (!InventorySystem.TryGetSlotEntity(entity, "back", out var back))
            return null;
        if (TryComp<StorageComponent>(back, out var storage))
            return (back.Value, storage);

        if (_containers.TryGetContainer(back.Value, ModSuitContainers.Modules, out var modules))
        {
            foreach (var module in modules.ContainedEntities)
                if (TryComp<StorageComponent>(module, out var internalStorage) && internalStorage.OpenOnActivate)
                    return (module, internalStorage);
        }

        if (!preview || !TryComp<ModStartingModulesComponent>(back, out var starting))
            return null;
        foreach (var module in starting.Modules)
        {
            if (!PrototypeManager.Index(module).TryGetComponent<StorageComponent>(out var internalStorage) ||
                !internalStorage.OpenOnActivate)
                continue;
            var previewModule = Spawn(module, Transform(back.Value).Coordinates);
            var container = _containers.EnsureContainer<Container>(back.Value, ModSuitContainers.Modules);
            if (_containers.Insert(previewModule, container))
                return (previewModule, Comp<StorageComponent>(previewModule));
            Del(previewModule);
        }

        return null;
    }

    public void FinishStartingGearStorage(
        EntityUid entity,
        List<StartingGearStorageItem> items,
        IReadOnlyDictionary<string, BackpackItemPlacement>? layout,
        Dictionary<string, EntityUid>? previewItems = null)
    {
        var backpack = GetBackpackStorage(entity, previewItems != null);
        var equipment = EntityManager.System<PersonalLoadoutEquipSystem>();

        // Reserve saved positions before filling the remaining space with defaults.
        foreach (var item in items.OrderBy(item => item.Key == null || layout?.ContainsKey(item.Key) != true))
        {
            if (item.Slot != "back")
            {
                if (previewItems == null)
                    GiveStartingGearStorageItem(entity, item, fallback: true);
                continue;
            }

            BackpackItemPlacement? requested = item.Key != null && layout != null && layout.TryGetValue(item.Key, out var placement)
                ? placement : null;
            if (requested?.Removed == true)
                continue;
            if (previewItems != null && backpack == null)
                continue;

            var spawned = Spawn(item.Prototype, _xformSystem.GetMapCoordinates(entity));
            equipment.Customize(spawned, item.Customization);
            if (previewItems != null && item.Key != null)
                previewItems.TryAdd(item.Key, spawned);

            if (backpack is { } storage)
            {
                if (requested is { } position &&
                    _storage.CanInsert(storage, spawned, out _, storage.Comp, ignoreStacks: true, ignoreLocation: true) &&
                    _storage.InsertAt((storage.Owner, storage.Comp), spawned,
                        new ItemStorageLocation(Angle.FromDegrees(position.Rotation), (position.X, position.Y)), out _,
                        playSound: false, stackAutomatically: false))
                    continue;

                if (_storage.Insert(storage, spawned, out _, storageComp: storage.Comp, playSound: false,
                        stackAutomatically: previewItems == null && layout?.Count is not > 0))
                    continue;
            }

            if (previewItems == null)
                _handsSystem.TryPickupAnyHand(entity, spawned, checkActionBlocker: false);
            else
                _containers.Insert(spawned, _containers.EnsureContainer<Container>(entity, "loadout-preview-overflow"), force: true);
        }
    }
}
