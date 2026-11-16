using System.Linq;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared.Station;

public abstract partial class SharedStationSpawningSystem
{
    [Dependency] private SharedContainerSystem _containers = default!;

    public const string DefaultSurvivalKit = "default/survival-kit";

    public static bool IsStartingSurvivalKit(StartingGearStorageItem item) => item.Slot == "back" && item.Prototype.Id is
        "BoxSurvivalMonoOxygenExtended" or "BoxSurvivalMonoNitrogenExtended" or "BoxSurvivalMonoVaporExtended";

    public static bool SurvivalKitRemoved(HumanoidCharacterProfile profile) =>
        profile.Loadouts.TryGetValue(PersonalLoadoutSystem.Role, out var personal) &&
        personal.BackpackLayout.GetValueOrDefault(DefaultSurvivalKit).Removed;

    public static bool IsBackpackLoadout(LoadoutPrototype item, IPrototypeManager prototypes)
    {
        prototypes.TryIndex(item.StartingGear, out var gear);
        return item.Components.Count == 0 && ContainsOnlyBackpackGear(item) &&
            (gear == null || ContainsOnlyBackpackGear(gear)) &&
            (item.Storage.Count > 0 || gear?.Storage.Count > 0);
    }

    private static bool ContainsOnlyBackpackGear(IEquipmentLoadout gear) =>
        gear.Equipment.Count == 0 && gear.Inhand.Count == 0 && gear.Storage.Keys.All(slot => slot == "back");

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
        StartingContainerContents? contents = null,
        bool preview = false,
        HumanoidCharacterProfile? profile = null)
    {
        contents ??= new StartingContainerContents();
        var trackContents = preview || layout?.Count > 0;
        if (GetBackpackStorage(entity, preview) is { } backpack)
        {
            if (trackContents)
                RegisterStartingContainer("", backpack.Owner, contents, preview);
            else
                contents.Containers[""] = backpack.Owner;
        }
        if (profile != null && contents.Containers.ContainsKey(""))
            EnsureSurvivalKit(items, profile);
        var skipKit = profile != null && (SurvivalKitRemoved(profile) || !contents.Containers.ContainsKey(""));
        var equipment = EntityManager.System<PersonalLoadoutEquipSystem>();
        var pending = new List<(EntityUid Item, BackpackItemPlacement? Placement)>();
        foreach (var item in items)
        {
            if (item.Slot != "back")
            {
                if (!preview)
                    GiveStartingGearStorageItem(entity, item, fallback: true);
                continue;
            }

            BackpackItemPlacement? requested = item.Key != null && layout != null && layout.TryGetValue(item.Key, out var placement)
                ? placement : null;
            if (requested?.Removed == true || skipKit && IsStartingSurvivalKit(item))
                continue;

            var spawned = Spawn(item.Prototype, _xformSystem.GetMapCoordinates(entity));
            equipment.Customize(spawned, item.Customization);
            if (trackContents && item.Key != null)
            {
                contents.Items.TryAdd(item.Key, (spawned, null));
                RegisterStartingContainer(item.Key, spawned, contents, preview);
            }
            pending.Add((spawned, requested));
        }

        // Lift all edited box contents before restoring positions so that moving
        // one piece into another piece's old cells also works at round start.
        if (layout != null)
        {
            foreach (var (key, item) in contents.Items.ToArray())
            {
                if (item.Origin == null || !layout.TryGetValue(key, out var edited) ||
                    !contents.Containers.TryGetValue(item.Origin, out var source) || !TryComp<StorageComponent>(source, out var box) ||
                    !_containers.Remove(item.Entity, box.Container, force: true, destination: Transform(entity).Coordinates))
                    continue;
                pending.Add((item.Entity, edited));
            }
        }

        foreach (var (item, placement) in pending.Where(entry => entry.Placement?.Removed == true))
            Del(item);
        foreach (var (item, placement) in pending.OrderBy(entry => entry.Placement == null))
        {
            if (placement?.Removed == true || !EntityManager.EntityExists(item))
                continue;
            if (contents.Containers.TryGetValue(placement?.Container ?? "", out var uid) && TryComp<StorageComponent>(uid, out var storage))
            {
                if (placement is { } position && _storage.CanInsert(uid, item, out _, storage, ignoreStacks: true, ignoreLocation: true) &&
                    _storage.InsertAt((uid, storage), item, new ItemStorageLocation(Angle.FromDegrees(position.Rotation), (position.X, position.Y)),
                        out _, playSound: false, stackAutomatically: false))
                    continue;
                if (placement == null && _storage.Insert(uid, item, out _, storageComp: storage, playSound: false,
                        stackAutomatically: !preview && layout?.Count is not > 0))
                    continue;
            }
            if (preview)
                _containers.Insert(item, _containers.EnsureContainer<Container>(entity, "loadout-preview-overflow"), force: true);
            else if (placement == null)
                _handsSystem.TryPickupAnyHand(entity, item, checkActionBlocker: false);
        }
    }

    private static void EnsureSurvivalKit(List<StartingGearStorageItem> items, HumanoidCharacterProfile profile)
    {
        var existing = items.FindIndex(IsStartingSurvivalKit);
        if (existing >= 0)
        {
            var selected = items[existing];
            items.RemoveAt(existing);
            items.Insert(0, selected);
            return;
        }

        var prototype = profile.Species.Id switch
        {
            "SlimePerson" or "Vox" => "BoxSurvivalMonoNitrogenExtended",
            "Feroxi" => "BoxSurvivalMonoVaporExtended",
            _ => "BoxSurvivalMonoOxygenExtended",
        };
        items.Insert(0, new StartingGearStorageItem("back", prototype, null, DefaultSurvivalKit));
    }

    public void RegisterStartingContainer(string key, EntityUid uid, StartingContainerContents contents, bool preview = false)
    {
        if (!TryComp<StorageComponent>(uid, out var storage) || !contents.Containers.TryAdd(key, uid))
            return;
        if (preview)
            _storage.FillLoadoutPreview((uid, storage));
        var occurrences = new Dictionary<string, int>();
        foreach (var item in storage.Container.ContainedEntities)
        {
            if (!TryComp<MetaDataComponent>(item, out var metadata) || metadata.EntityPrototype is not { } prototype)
                continue;
            var index = occurrences.GetValueOrDefault(prototype.ID);
            occurrences[prototype.ID] = index + 1;
            var itemKey = $"{key}/contents/{prototype.ID}/{index}";
            contents.Items.TryAdd(itemKey, (item, key));
            RegisterStartingContainer(itemKey, item, contents, preview);
        }
    }
}

public sealed class StartingContainerContents
{
    public readonly Dictionary<string, EntityUid> Containers = new();
    public readonly Dictionary<string, (EntityUid Entity, string? Origin)> Items = new();
}
