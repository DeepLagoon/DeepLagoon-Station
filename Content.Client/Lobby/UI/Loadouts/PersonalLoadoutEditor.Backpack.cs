using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Station;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI.Loadouts;

public sealed partial class PersonalLoadoutEditor
{
    private EntityUid? _backpackPreview;
    private HumanoidCharacterProfile? _backpackPreviewProfile;
    private string? _backpackPreviewJob;
    private StartingContainerContents _backpackContents = new();
    private string _boxView = "";
    private readonly List<StartingGearStorageItem> _backpackDefaults = new();
    private readonly Dictionary<string, BackpackChoice> _backpackChoices = new();
    private TguiData? _backpackState;

    private sealed record BackpackChoice(StartingGearStorageItem Item, LoadoutPrototype? Loadout, LoadoutGroupPrototype? Group,
        string Category, bool CanSelect, bool Allowed, string? Reason);

    public void CloseBackpackPreview(bool keepContainer = false)
    {
        _entities.DeleteEntity(_backpackPreview);
        _backpackPreview = null;
        _backpackPreviewProfile = null;
        _backpackContents = new();
        if (!keepContainer)
            _boxView = "";
        _backpackDefaults.Clear();
        _backpackChoices.Clear();
        _backpackState = null;
    }

    public TguiData CreateTguiBackpack(Func<EntityUid> createPreview)
    {
        if (_profile == null || !_prototypes.TryIndex<JobPrototype>(_job, out var job))
            return new TguiData();
        if (_backpackState != null && ReferenceEquals(_profile, _backpackPreviewProfile) && _job == _backpackPreviewJob)
            return _backpackState;

        CloseBackpackPreview(keepContainer: true);
        _backpackPreviewProfile = _profile;
        _backpackPreviewJob = _job;
        PrepareJobCatalog();
        var state = _backpackState = new TguiData();
        if (_jobRole == null)
            return state;

        var preview = createPreview();
        _backpackPreview = preview;
        var spawning = _entities.System<StationSpawningSystem>();
        var equipment = _entities.System<PersonalLoadoutEquipSystem>();
        var role = _prototypes.Index(_jobRole.Role);
        foreach (var (_, items) in _jobRole.SelectedLoadouts.OrderBy(group => role.Groups.IndexOf(group.Key)))
        {
            foreach (var selected in items)
            {
                if (!_prototypes.TryIndex(selected.Prototype, out var item))
                    continue;
                if (_prototypes.TryIndex(item.StartingGear, out var gear))
                    AddStartingGear(gear, selected.Customization);
                AddStartingGear(item, selected.Customization);
            }
        }
        if (_prototypes.TryIndex(job.StartingGear, out var jobGear))
            AddStartingGear(jobGear, null);

        equipment.Apply(preview, _profile, _job, _session, preview: true, deferredStorage: _backpackDefaults);
        var containers = _entities.System<SharedContainerSystem>();
        foreach (var choice in PreviewHands(_jobRole, GetRole()).Values)
        {
            var item = choice.Item;
            if (item.Key == null || !_prototypes.Index(item.Prototype).TryGetComponent<StorageComponent>(out _))
                continue;
            var box = _entities.SpawnEntity(item.Prototype, MapCoordinates.Nullspace);
            if (!containers.Insert(box, containers.EnsureContainer<Container>(preview, "loadout-preview-hand-boxes"), force: true))
            {
                _entities.DeleteEntity(box);
                continue;
            }
            equipment.Customize(box, item.Customization);
            spawning.RegisterStartingContainer(item.Key, box, _backpackContents, preview: true);
        }

        var layout = _jobRole.BackpackLayout;
        spawning.FinishStartingGearStorage(preview, _backpackDefaults, layout, _backpackContents, preview: true);
        BuildBackpackCatalog(equipment, layout);
        state.Array("containers", _backpackContents.Containers
            .Where(entry => entry.Key.Length > 0 && _entities.EntityExists(entry.Value))
            .Select(entry => new TguiData().String("key", entry.Key)
                .String("name", _entities.GetComponent<MetaDataComponent>(entry.Value).EntityName)));
        if (_boxView != "" && (!_backpackContents.Containers.TryGetValue(_boxView, out var opened) || !_entities.EntityExists(opened)))
            _boxView = "";
        state.String("container", _boxView);
        if (!_backpackContents.Containers.TryGetValue(_boxView, out var target) ||
            !_entities.TryGetComponent<StorageComponent>(target, out var bag) || bag.Grid.Count == 0)
            return state.String("status", "У выбранного снаряжения нет хранилища на спине.");

        var live = _backpackContents.Items.Where(entry => _entities.EntityExists(entry.Value.Entity)).ToArray();
        var itemsInGrid = live.Where(entry => TargetContainer(entry.Key, entry.Value.Origin, layout) == _boxView)
            .Select(entry => BackpackPreviewItem(entry.Key, entry.Value.Entity, bag.StoredItems.GetValueOrDefault(entry.Value.Entity),
                !bag.StoredItems.ContainsKey(entry.Value.Entity), false));
        var trackedEntities = live.Select(entry => entry.Value.Entity).ToHashSet();
        var fixedItems = bag.StoredItems.Where(entry => !trackedEntities.Contains(entry.Key))
            .Select(entry => BackpackPreviewItem("fixed/" + entry.Key, entry.Key, entry.Value, false, true));
        var tileSuffix = IoCManager.Resolve<IConfigurationManager>().GetCVar(CCVars.OpaqueStorageWindow) ? "_opaque.png" : ".png";
        return state.String("name", _entities.GetComponent<MetaDataComponent>(target).EntityName)
            .String("tile", ThemeImage("Storage/tile_empty" + tileSuffix))
            .String("blockedTile", ThemeImage("Storage/tile_blocked" + tileSuffix))
            .String("pieceTile", ThemeImage("Storage/piece_center.png"))
            .Array("grid", bag.Grid.Select(BackpackBox))
            .Array("items", itemsInGrid.Concat(fixedItems))
            .Array("catalog", _backpackChoices.Where(choice => !_backpackContents.Items.TryGetValue(choice.Key, out var item) ||
                    !_entities.EntityExists(item.Entity)).Select(choice => BackpackCatalogItem(choice.Value))
                .Concat(live.Where(entry => TargetContainer(entry.Key, entry.Value.Origin, layout) != _boxView &&
                        _boxView != entry.Key && !_boxView.StartsWith(entry.Key + "/contents/", StringComparison.Ordinal))
                    .Select(entry => BackpackPreviewItem(entry.Key, entry.Value.Entity, default, false, false)
                        .Bool("canSelect", true).Bool("transfer", true).String("categoryName", "Переложить из другого контейнера"))));

        void AddStartingGear(IEquipmentLoadout gear, PersonalLoadoutCustomization? customization)
        {
            _backpackDefaults.AddRange(SharedStationSpawningSystem.GetStartingGearStorageItems(gear, customization)
                .Where(item => item.Key != null));
            if (_jobRole.UnequippedSlots.Contains("back") ||
                !_entities.System<InventorySystem>().TryGetSlots(preview, out var slots) ||
                !slots.Any(slot => slot.Name == "back") ||
                _entities.System<InventorySystem>().TryGetSlotEntity(preview, "back", out _) ||
                gear.GetGear("back") is not { Length: > 0 } backpackId)
                return;

            var bag = _entities.SpawnEntity(backpackId, MapCoordinates.Nullspace);
            equipment.Customize(bag, customization);
            if (!_entities.System<InventorySystem>().TryEquip(preview, bag, "back", silent: true, force: true))
                _entities.DeleteEntity(bag);
        }
    }

    private static string TargetContainer(string key, string? origin, Dictionary<string, BackpackItemPlacement> layout) =>
        layout.TryGetValue(key, out var placement) ? placement.Container ?? "" : origin ?? "";

    private void BuildBackpackCatalog(PersonalLoadoutEquipSystem equipment, Dictionary<string, BackpackItemPlacement> layout)
    {
        var profile = _profile!;
        var role = _jobRole!;
        foreach (var entry in _jobEntries)
        {
            var item = entry.Item;
            _prototypes.TryIndex(item.StartingGear, out var gear);
            if (!ContainsOnlyBackpackGear(item) || item.Components.Count > 0 || gear != null && !ContainsOnlyBackpackGear(gear))
                continue;
            var allowed = role.IsValid(profile, _session, item.ID, IoCManager.Instance!, out var reason);
            var canSelect = allowed && CanAffordJob(role, entry.Group, item);
            var contents = SharedStationSpawningSystem.GetStartingGearStorageItems(item);
            if (gear != null)
                contents = contents.Concat(SharedStationSpawningSystem.GetStartingGearStorageItems(gear));
            foreach (var content in contents.Where(content => content.Key != null))
                Add(content, item, entry.Group, entry.Category, canSelect, allowed, reason?.ToString());
        }

        var system = _entities.System<PersonalLoadoutSystem>();
        var personal = GetRole();
        foreach (var item in system.Catalogue(profile, _job, _session))
        {
            if (item.PersonalItems.Count == 0 || !Enumerable.Range(0, item.PersonalItems.Count).All(i => equipment.IsBackpackItem(item, i)))
                continue;
            var allowed = system.CanUse(item, profile, _job, _session, out var reason);
            var canSelect = allowed && CanAffordPersonal(item, personal);
            var category = PersonalCategory(item);
            for (var i = 0; i < item.PersonalItems.Count; i++)
                Add(PersonalLoadoutEquipSystem.GetBackpackItem(item, i), item, null, category, canSelect, allowed, reason);
        }

        foreach (var item in _backpackDefaults)
        {
            if (item.Key != null && layout.TryGetValue(item.Key, out var placement) && placement.Removed)
                Add(item, null, null, "Items", true, true, null);
        }

        void Add(StartingGearStorageItem item, LoadoutPrototype? loadout, LoadoutGroupPrototype? group,
            string category, bool canSelect, bool allowed, string? reason)
        {
            if (_prototypes.Index(item.Prototype).TryGetComponent<ItemComponent>(out _))
                _backpackChoices.TryAdd(item.Key!, new BackpackChoice(item, loadout, group, category, canSelect, allowed, reason));
        }
    }

    private TguiData BackpackCatalogItem(BackpackChoice choice)
    {
        var entity = _prototypes.Index(choice.Item.Prototype);
        var item = entity.TryGetComponent<ItemComponent>(out var component) ? component : null;
        return new TguiData().String("key", choice.Item.Key).String("id", entity.ID).String("name", entity.Name)
            .String("iconId", "item/" + entity.ID).String("group", choice.Group?.ID)
            .String("categoryName", Loc.TryGetString("loadout-category-" + choice.Category, out var category) ? category : choice.Category)
            .String("reason", choice.Reason).Bool("allowed", choice.Allowed).Bool("canSelect", choice.CanSelect)
            .Bool("jobItem", choice.Group != null).Number("cost", choice.Group != null ? choice.Loadout!.Price : choice.Loadout?.PersonalCost ?? 0)
            .Array("shape", item == null ? [] : _entities.System<SharedItemSystem>().GetItemShape(item).Select(BackpackBox));
    }

    private TguiData BackpackPreviewItem(string key, EntityUid entity, ItemStorageLocation location, bool overflow, bool fixedItem)
    {
        var metadata = _entities.GetComponent<MetaDataComponent>(entity);
        return new TguiData().String("key", key).String("name", metadata.EntityName)
            .Bool("container", _backpackContents.Containers.ContainsKey(key))
            .String("iconId", metadata.EntityPrototype is { } prototype ? "item/" + prototype.ID : null)
            .Bool("overflow", overflow).Bool("fixed", fixedItem)
            .Number("x", location.Position.X).Number("y", location.Position.Y)
            .Number("rotation", ((int) Math.Round(location.Rotation.Degrees) + 360) % 360)
            .Array("shape", _entities.System<SharedItemSystem>().GetItemShape(entity).Select(BackpackBox));
    }

    private static TguiData BackpackBox(Box2i box) => new TguiData()
        .Number("left", box.Left).Number("bottom", box.Bottom).Number("right", box.Right).Number("top", box.Top);

    public bool HandleTguiBackpackAction(string action, TguiActionData args)
    {
        if (_profile == null || _jobRole == null || _backpackPreview == null ||
            !ReferenceEquals(_profile, _backpackPreviewProfile) || _job != _backpackPreviewJob ||
            args.String("key") is not { } key || key.Length > 512)
            return false;

        if (action == "backpack-container")
        {
            if (key != "" && (!_backpackContents.Containers.TryGetValue(key, out var box) || !_entities.EntityExists(box)))
                return false;
            _boxView = key;
            _backpackState = null;
            return true;
        }
        if (!_backpackContents.Containers.TryGetValue(_boxView, out var target) ||
            !_entities.TryGetComponent<StorageComponent>(target, out var storage))
            return false;

        var profile = _profile;
        var role = _jobRole.Clone();
        var remove = action == "backpack-remove";
        BackpackItemPlacement placement;
        if (remove)
        {
            placement = new BackpackItemPlacement(true, 0, 0, 0);
        }
        else
        {
            if (!args.TryInt("x", out var x) || !args.TryInt("y", out var y) ||
                !args.TryInt("rotation", out var rotation) || rotation is not (0 or 90 or 180 or 270) ||
                x is < -256 or > 256 || y is < -256 or > 256 || !storage.Grid.Any(box => box.Contains((x, y))))
                return false;
            placement = new BackpackItemPlacement(false, x, y, rotation, _boxView == "" ? null : _boxView);
        }

        if (action == "backpack-add")
        {
            if (_backpackContents.Items.TryGetValue(key, out var existing) && _entities.EntityExists(existing.Entity) ||
                !_backpackChoices.TryGetValue(key, out var choice) || !choice.CanSelect)
                return false;

            var previewItem = _entities.SpawnEntity(choice.Item.Prototype, MapCoordinates.Nullspace);
            var storageSystem = _entities.System<SharedStorageSystem>();
            var location = new ItemStorageLocation(Angle.FromDegrees(placement.Rotation), (placement.X, placement.Y));
            var fits = storageSystem.CanInsert(target, previewItem, out var reason, storage, ignoreStacks: true, ignoreLocation: true) &&
                       storageSystem.ItemFitsInGridLocation(previewItem, (target, storage), location);
            _entities.DeleteEntity(previewItem);
            if (!fits)
            {
                _backpackState?.String("status", reason == null ? "В выбранном месте нет свободных ячеек." : Loc.GetString(reason));
                return false;
            }

            if (choice.Loadout is { } loadout)
            {
                if (choice.Group is { } group)
                {
                    if (!role.SelectedLoadouts[group.ID].Any(item => item.Prototype.Id == loadout.ID))
                        role.AddLoadout(group.ID, loadout.ID, _prototypes);
                }
                else
                {
                    var personal = GetRole();
                    var personalGroup = _prototypes.Index<RoleLoadoutPrototype>(PersonalLoadoutSystem.Role).Groups
                        .First(id => _prototypes.Index(id).Loadouts.Contains(loadout.ID));
                    if (!personal.SelectedLoadouts[personalGroup].Any(item => item.Prototype.Id == loadout.ID))
                        personal.AddLoadout(personalGroup, loadout.ID, _prototypes);
                    profile = profile.WithLoadout(personal);
                }
            }
        }
        else if (!_backpackContents.Items.TryGetValue(key, out var item) || !_entities.EntityExists(item.Entity) ||
                 !remove && !CanMoveItem(item.Entity) ||
                 remove && TargetContainer(key, item.Origin, role.BackpackLayout) != _boxView)
        {
            return false;
        }

        // Deselect an optional choice once all of its items have been removed.
        if (remove && _backpackChoices.TryGetValue(key, out var removed) && removed.Loadout is { } removedLoadout &&
            _backpackChoices.Values.Where(choice => choice.Loadout == removedLoadout && choice.Group == removed.Group)
                .All(choice => choice.Item.Key == key || role.BackpackLayout.GetValueOrDefault(choice.Item.Key!).Removed))
        {
            if (removed.Group is { } group && role.SelectedLoadouts[group.ID].Count > group.MinLimit &&
                role.RemoveLoadout(group.ID, removedLoadout.ID, _prototypes))
            {
                ClearRemovedLoadoutPositions();
                ProfileChanged?.Invoke(profile.WithLoadout(role));
                return true;
            }

            if (removed.Group == null)
            {
                var personal = GetRole();
                var personalGroup = personal.SelectedLoadouts.FirstOrDefault(group =>
                    group.Value.Any(choice => choice.Prototype.Id == removedLoadout.ID));
                if (personalGroup.Value != null && personalGroup.Value.Count > _prototypes.Index(personalGroup.Key).MinLimit)
                {
                    personal.RemoveLoadout(personalGroup.Key, removedLoadout.ID, _prototypes);
                    ClearRemovedLoadoutPositions();
                    ProfileChanged?.Invoke(profile.WithLoadout(personal).WithLoadout(role));
                    return true;
                }
            }

            void ClearRemovedLoadoutPositions()
            {
                foreach (var selection in _backpackChoices.Values.Where(choice => choice.Loadout == removedLoadout && choice.Group == removed.Group))
                    role.BackpackLayout.Remove(selection.Item.Key!);
            }
        }

        if (role.BackpackLayout.Count >= 256 && !role.BackpackLayout.ContainsKey(key))
            return false;
        role.BackpackLayout[key] = placement;
        ProfileChanged?.Invoke(profile.WithLoadout(role));
        return true;

        bool CanMoveItem(EntityUid moving)
        {
            if (_boxView == key || _boxView.StartsWith(key + "/contents/", StringComparison.Ordinal))
                return false;
            var system = _entities.System<SharedStorageSystem>();
            var location = new ItemStorageLocation(Angle.FromDegrees(placement.Rotation), (placement.X, placement.Y));
            return (storage.StoredItems.ContainsKey(moving) || system.CanInsert(target, moving, out _, storage, ignoreStacks: true, ignoreLocation: true))
                   && system.ItemFitsInGridLocation(moving, (target, storage), location);
        }
    }

    private static bool ContainsOnlyBackpackGear(IEquipmentLoadout gear) =>
        gear.Equipment.Count == 0 && gear.Inhand.Count == 0 && gear.Storage.Keys.All(slot => slot == "back");
}
