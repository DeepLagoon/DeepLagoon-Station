using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Clothing;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Inventory;
using Content.Shared._NF.Bank;

namespace Content.Client.Lobby.UI.Loadouts;

public sealed partial class PersonalLoadoutEditor
{
    public bool TguiMode;
    private static TguiSpriteImages _images => TguiSpriteImages.Shared; // DeepLagoon: tgui fix, one session cache

    public IEnumerable<TguiData> CreateTguiSlots(EntityUid preview)
    {
        var inventory = _entities.System<InventorySystem>();
        if (!_entities.EntityExists(preview) || !inventory.TryGetSlots(preview, out var slots)) yield break;
        foreach (var slot in FilterPreviewSlots(slots)
                     .OrderBy(s => s.StrippingWindowPos.Y).ThenBy(s => s.StrippingWindowPos.X))
            yield return new TguiData().String("id", slot.Name).String("name", SlotLabel(slot)).Bool("selected", _slotFilter?.Name == slot.Name).String("background", _images.Frame(new Robust.Shared.Utility.SpriteSpecifier.Texture(new Robust.Shared.Utility.ResPath("Interface/Default/Slots/" + slot.TextureName + ".png"))))
                .Array("images", inventory.TryGetSlotEntity(preview, slot.Name, out var item) ? _images.EntityImages(item.Value) : Enumerable.Empty<TguiData>());
    }

    public bool SelectTguiSlot(EntityUid preview, string name)
    {
        var inventory = _entities.System<InventorySystem>();
        if (!inventory.TryGetSlots(preview, out var slots)) return false;
        var slot = FilterPreviewSlots(slots).FirstOrDefault(s => s.Name == name);
        if (slot == null) return false;
        _slotFilter = slot;
        _pendingSlotCategory = null;
        UpdateSlotHighlights();
        SlotOpened?.Invoke();
        return true;
    }

    public bool RemoveTguiSlot(EntityUid preview, string name)
    {
        if (_profile == null || !_entities.EntityExists(preview)) return false;
        var inventory = _entities.System<InventorySystem>();
        if (!inventory.TryGetSlots(preview, out var slots)
            || !slots.Any(s => s.Name == name && (s.SlotFlags & (SlotFlags.PREVENTEQUIP | SlotFlags.POCKET | SlotFlags.SUITSTORAGE)) == 0)
            || !inventory.TryGetSlotEntity(preview, name, out var entity)
            || _entities.GetComponent<MetaDataComponent>(entity.Value).EntityPrototype is not { } item) return false;

        // Remember the empty slot even when the item came from mandatory/hidden
        // job defaults or base starting gear. Removing a selection alone would
        // allow validation or preview rebuilding to restore that gear.
        var personal = GetRole();
        foreach (var choices in personal.SelectedLoadouts.Values)
            choices.RemoveAll(c => _prototypes.TryIndex(c.Prototype, out var proto)
                && proto.PersonalItems.Any(id => id.Id == item.ID));
        PrepareJobCatalog();
        var role = _jobRole?.Clone() ?? personal;
        role.UnequippedSlots.Add(name);
        _cachedTguiState = null;
        ProfileChanged?.Invoke(_profile.WithLoadout(personal).WithLoadout(role));
        return true;
    }

    private HumanoidCharacterProfile? _cachedTguiProfile;
    private string? _cachedTguiJob;
    private SlotDefinition? _cachedTguiSlot;
    private TguiData? _cachedTguiState;
    private int _cachedTguiPoints;
    private long _cachedTguiMoney = -1;

    public TguiData CreateTguiState()
    {
        var system = _entities.System<PersonalLoadoutSystem>();
        var points = system.GetPoints(_session);
        var money = _coins.GetLastKnownBalance();
        if (_cachedTguiPoints == points && _cachedTguiMoney == money && _cachedTguiState != null && ReferenceEquals(_profile, _cachedTguiProfile) && _job == _cachedTguiJob && ReferenceEquals(_slotFilter, _cachedTguiSlot)) return _cachedTguiState;
        _cachedTguiPoints = points;
        _cachedTguiMoney = money;
        _cachedTguiProfile = _profile;
        _cachedTguiJob = _job;
        _cachedTguiSlot = _slotFilter;
        var data = new TguiData();
        if (_profile == null) return data;
        PrepareJobCatalog();
        var role = GetRole();
        var selected = role.SelectedLoadouts.Values.SelectMany(group => group).ToDictionary(item => item.Prototype.Id);
        var spent = selected.Values.Sum(item => _prototypes.Index(item.Prototype).PersonalCost);
        data.String("job", _job).String("roleName", _jobRole?.EntityName).Bool("customRoleName", _roleName.Visible)
            .String("balance", BankSystemExtensions.ToSpesoString(_profile.BankBalance))
            .String("savings", BankSystemExtensions.ToSpesoString(_coins.GetLastKnownBalance()))
            .String("cost", BankSystemExtensions.ToSpesoString(_jobRole?.SelectedLoadouts.Values.SelectMany(items => items)
                .Sum(item => _prototypes.TryIndex(item.Prototype, out var proto) && !system.FullyUnequipped(proto, _jobRole!.UnequippedSlots) ? proto.Price : 0) ?? 0))
            .Number("points", Math.Max(0, system.GetPoints(_session) - spent)).Number("maxPoints", system.GetPoints(_session))
            .String("slot", _slotFilter == null ? "" : SlotLabel(_slotFilter))
            .Array("jobs", _equipmentJobs.Select(id => new TguiData().String("id", id).String("name", _prototypes.Index<JobPrototype>(id).LocalizedName)))
            .Array("categories", _prototypes.EnumeratePrototypes<PersonalLoadoutCategoryPrototype>().Select(category =>
                new TguiData().String("id", category.ID).String("name", Loc.TryGetString("loadout-category-" + category.ID, out var name) ? name : category.ID)
                    .Array("children", category.SubCategories.Select(id => new TguiData().String("id", id.Id))).Bool("root", category.Root)))
            .Array("items", _prototypes.EnumeratePrototypes<LoadoutPrototype>().Where(item => item.PersonalItems.Count > 0 && (_slotFilter == null || FitsSlot(item, _slotFilter)))
                .Select(item =>
                {
                    var allowed = system.CanUse(item, _profile, _job, _session, out var reason);
                    selected.TryGetValue(item.ID, out var choice);
                    var custom = choice?.Customization ?? role.Customizations.GetValueOrDefault(item.ID);
                    var donor = DonorCategoryOf(item);
                    var donorText = donor == DonorCategory.Boosty ? Loc.GetString("dl-loadout-donor-boosty", ("tier", item.PersonalDonorTier))
                        : donor == DonorCategory.DiscordBoost ? Loc.GetString("dl-loadout-donor-discord") : "";
                    return new TguiData().String("id", item.ID).String("name", _prototypes.Index(item.PersonalItems[0]).Name)
                        .String("category", item.PersonalCategory).Bool("selected", choice != null).Bool("allowed", allowed)
                        .Bool("canSelect", choice != null || allowed && CanAffordPersonal(item, role))
                        .String("reason", reason).String("donor", donorText).Number("cost", item.PersonalCost)
                        .Bool("canPaint", true).Bool("canRename", true).Bool("canDescribe", true)
                        .Bool("canHeirloom", item.PersonalHeirloom).Bool("heirloom", choice?.Customization?.Heirloom ?? false)
                        .String("color", custom?.Color).String("customName", custom?.Name).String("description", custom?.Description)
                        .String("iconId", "item/" + item.PersonalItems[0].Id);
                }))
            .Array("jobItems", _jobEntries.Where(entry => _slotFilter == null || FitsJobSlot(entry.Item, _slotFilter)).Select(entry =>
            {
                var item = entry.Item;
                var valid = _jobRole!.IsValid(system.RemovePersonalConflicts(_profile, item), _session, item.ID, IoCManager.Instance!, out var reason);
                var chosen = _jobRole.SelectedLoadouts.TryGetValue(entry.Group.ID, out var choices) && choices.Any(choice => choice.Prototype.Id == item.ID)
                    && !system.HasUnequippedSlots(_profile, _job, item);
                var entity = item.PreviewEntity ?? item.DummyEntity ?? _entities.System<LoadoutSystem>().GetFirstOrNull(item);
                var custom = _jobRole.Customizations.GetValueOrDefault(item.ID) ?? _jobRole.SelectedLoadouts.Values.SelectMany(items => items).FirstOrDefault(choice => choice.Prototype.Id == item.ID)?.Customization;
                return new TguiData().String("id", item.ID).String("group", entry.Group.ID).String("groupName", Loc.GetString(entry.Group.Name))
                    .String("name", JobItemName(item)).String("category", entry.Category).Bool("selected", chosen).Bool("allowed", valid).Bool("canSelect", valid || chosen)
                    .String("reason", reason?.ToString()).Number("cost", item.Price).Number("min", entry.Group.MinLimit).Number("max", entry.Group.MaxLimit)
                    .Bool("canPaint", true).Bool("canRename", true).Bool("canDescribe", true)
                    .String("color", custom?.Color).String("customName", custom?.Name).String("description", custom?.Description)
                    .String("iconId", entity is {} id ? "item/" + id.Id : "");
            }));
        _cachedTguiState = data;
        return data;
    }

    public bool HandleTguiAction(string action, TguiActionData args)
    {
        _cachedTguiState = null;
        if (_profile == null) return false;
        if (action == "all-slots") { _slotFilter = null; UpdateSlotHighlights(); return true; }
        if (action == "remove-unavailable") { RemoveUnavailable(); return true; }
        if (action == "job")
        {
            if (args.String("value") is not {} job || !_equipmentJobs.Contains(job)) return false;
            JobChanged?.Invoke(job); return true;
        }
        if (action == "role-name")
        {
            if (_jobRole == null || !_roleName.Visible || args.String("value") is not {} name || name.Length > HumanoidCharacterProfile.MaxLoadoutNameLength) return false;
            _jobRole.EntityName = name; RoleNameChanged?.Invoke(_jobRole); return true;
        }
        if (args.String("id") is not {} id) return false;
        if (action is "paint" or "rename" or "heirloom")
        {
            var jobItem = args.String("jobItem") == "true";
            var entry = _jobEntries.FirstOrDefault(entry => entry.Item.ID == id && entry.Group.ID == args.String("group"));
            if (!_prototypes.TryIndex<LoadoutPrototype>(id, out var item) || (jobItem ? entry == null || _jobRole == null : item.PersonalItems.Count == 0)) return false;
            var target = jobItem ? _jobRole!.Clone() : GetRole();
            var custom = target.Customizations.GetValueOrDefault(id) ?? target.SelectedLoadouts.Values.SelectMany(items => items).FirstOrDefault(choice => choice.Prototype.Id == id)?.Customization ?? new PersonalLoadoutCustomization();
            var color = action == "paint" ? args.String("value") : custom.Color;
            if (!string.IsNullOrEmpty(color) && Color.TryFromHex(color) == null) return false;
            var updatedCustom = _entities.System<PersonalLoadoutSystem>().Sanitize(item, new PersonalLoadoutCustomization
            {
                Color = color, Name = action == "rename" ? args.String("name") : custom.Name,
                Description = action == "rename" ? args.String("description") : custom.Description,
                Heirloom = action == "heirloom" ? !custom.Heirloom : custom.Heirloom,
            })!;
            target.Customizations[id] = updatedCustom;
            foreach (var group in target.SelectedLoadouts.Values)
                for (var i = 0; i < group.Count; i++)
                    if (group[i].Prototype.Id == id) group[i] = new Loadout { Prototype = id, Customization = updatedCustom };
            ProfileChanged?.Invoke(_profile.WithLoadout(target)); return true;
        }
        if (action == "job-select")
        {
            var entry = _jobEntries.FirstOrDefault(entry => entry.Item.ID == id && entry.Group.ID == args.String("group"));
            if (entry == null || _jobRole == null) return false;
            var role = _jobRole.Clone();
            var profile = _profile;
            var stored = role.SelectedLoadouts.TryGetValue(entry.Group.ID, out var choices) && choices.Any(choice => choice.Prototype.Id == id);
            var slotSystem = _entities.System<PersonalLoadoutSystem>();
            var chosen = stored && !slotSystem.HasUnequippedSlots(profile, _job, entry.Item);
            if (chosen) role.RemoveLoadout(entry.Group.ID, id, _prototypes);
            else
            {
                profile = _entities.System<PersonalLoadoutSystem>().RemovePersonalConflicts(profile, entry.Item);
                if (!role.IsValid(profile, _session, id, IoCManager.Instance!, out _)) return false;
                if (!stored) role.AddLoadout(entry.Group.ID, id, _prototypes);
            }
            profile = profile.WithLoadout(role);
            if (!chosen) profile = slotSystem.RestoreEquipmentSlots(profile, _job, entry.Item);
            ProfileChanged?.Invoke(profile); return true;
        }
        if (!_prototypes.TryIndex<LoadoutPrototype>(id, out var prototype) || prototype.PersonalItems.Count == 0) return false;
        var updated = GetRole();
        var selected = updated.SelectedLoadouts.Values.SelectMany(group => group).FirstOrDefault(item => item.Prototype.Id == id);
        var system = _entities.System<PersonalLoadoutSystem>();
        if (action == "select")
        {
            if (selected == null && (!system.CanUse(prototype, _profile, _job, _session, out _) ||
                !CanAffordPersonal(prototype, updated))) return false;
            Select(prototype, selected == null); return true;
        }
        return false;
    }
}
