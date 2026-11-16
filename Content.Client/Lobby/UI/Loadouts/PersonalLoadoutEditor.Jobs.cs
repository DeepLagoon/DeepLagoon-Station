using Content.Client._DeepLagoon.Search; // DeepLagoon: fuzzy search
using System.Linq;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Containers;
using Content.Shared.Clothing;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Storage;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI.Loadouts;

public sealed partial class PersonalLoadoutEditor
{
    private readonly OptionButton _jobSelector = new() { HorizontalExpand = true };
    private readonly List<string> _equipmentJobs = new();
    private readonly Label _jobBalance = new();
    private readonly Label _jobCost = new();
    private readonly Label _jobCount = new();
    private readonly LineEdit _roleName = new() { HorizontalExpand = true };
    private readonly List<JobEntry> _jobEntries = new();
    private RoleLoadout? _jobRole;
    private sealed record JobEntry(LoadoutGroupPrototype Group, LoadoutPrototype Item, string Category);
    public event Action<string?>? JobChanged;
    public event Action<RoleLoadout>? RoleNameChanged;

    private void InitializeJobBrowser()
    {
        var header = new BoxContainer { HorizontalExpand = true };
        header.AddChild(new Label { Text = Loc.GetString("dl-loadout-job-equipment") });
        header.AddChild(_jobSelector);
        AddChild(header);
        AddChild(_jobBalance);
        AddChild(_jobCost);
        AddChild(_jobCount);
        _roleName.PlaceHolder = Loc.GetString("loadout-name-edit-label");
        _roleName.IsValid = text => text.Length <= Content.Shared.Preferences.HumanoidCharacterProfile.MaxLoadoutNameLength;
        AddChild(_roleName);
        _roleName.OnTextChanged += args =>
        {
            if (_refreshing || _jobRole == null)
                return;
            _jobRole.EntityName = args.Text;
            RoleNameChanged?.Invoke(_jobRole);
        };
        _jobSelector.OnItemSelected += args =>
        {
            _jobSelector.SelectId(args.Id);
            JobChanged?.Invoke(_equipmentJobs[args.Id]);
        };
    }

    private void PrepareJobCatalog()
    {
        _jobEntries.Clear();
        _jobRole = null;
        _equipmentJobs.Clear();
        _jobSelector.Clear();
        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>().Where(j => j.SetPreference && _prototypes.HasIndex<RoleLoadoutPrototype>(LoadoutSystem.GetJobPrototype(j.ID))).OrderBy(j => j.LocalizedName))
        {
            var index = _equipmentJobs.Count;
            _equipmentJobs.Add(job.ID);
            _jobSelector.AddItem(job.LocalizedName, index);
            if (job.ID == _job)
                _jobSelector.SelectId(index);
        }
        _jobBalance.Text = Loc.GetString("frontier-loadout-balance", ("balance", BankSystemExtensions.ToSpesoString(_profile!.BankBalance)));
        _roleName.Visible = false;
        if (_prototypes.TryIndex<RoleLoadoutPrototype>(LoadoutSystem.GetJobPrototype(_job), out var proto))
        {
            _jobRole = _profile.GetLoadoutOrDefault(proto.ID, _session, _profile.Species, _entities, _prototypes).Clone();
            _jobRole.EnsureValid(_profile, _session, IoCManager.Instance!);
            _roleName.Visible = proto.CanCustomizeName;
            _roleName.Text = _jobRole.EntityName ?? string.Empty;
            foreach (var groupId in proto.Groups)
            {
                var group = _prototypes.Index(groupId);
                if (group.Hidden)
                    continue;
                var ids = group.Loadouts.Concat(group.Subgroups.SelectMany(id => _prototypes.Index(id).Loadouts)).Distinct();
                foreach (var id in ids)
                {
                    if (!_prototypes.TryIndex(id, out var item) || _jobRole.IsHidden(_profile, _session, id, IoCManager.Instance!))
                        continue;
                    _jobEntries.Add(new JobEntry(group, item, JobCategory(group, item)));
                }
            }
        }
        var cost = _jobRole == null ? 0 : JobCost(_jobRole);
        _jobCost.Text = Loc.GetString("frontier-loadout-cost", ("cost", BankSystemExtensions.ToSpesoString(cost)));
        _jobCount.Text = Loc.GetString("dl-loadout-job-count", ("count", _jobEntries.Count));
    }

    private IEnumerable<JobEntry> FilteredJobEntries(string? category = null)
    {
        if (_profile == null || _jobRole == null)
            return Enumerable.Empty<JobEntry>();
        // DeepLagoon: fuzzy search; the category is applied after the text match, so the typo rule sees the whole list
        var entries = _jobEntries
            .Where(x => _slotFilter == null || FitsJobSlot(x.Item, _slotFilter))
            .Where(x => _showUnavailable.Pressed || _jobRole.SelectedLoadouts.TryGetValue(x.Group.ID, out var selected) && selected.Any(i => i.Prototype.Id == x.Item.ID) || _jobRole.IsValid(_entities.System<Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutSystem>().RemovePersonalConflicts(_profile, x.Item), _session, x.Item.ID, IoCManager.Instance!, out _));
        return FuzzySearch.Filter(entries.ToList(), _search.Text, x => JobItemName(x.Item), x => x.Item.ID)
            .Where(x => category == null || x.Category == category);
    }

    private int AddJobCards(GridContainer grid, string? category)
    {
        if (_jobRole == null || _profile == null)
            return 0;
        var selectedCount = 0;
        foreach (var entry in FilteredJobEntries(category).OrderBy(x => JobItemName(x.Item)))
        {
                var role = _jobRole;
                var item = entry.Item;
                var stored = role.SelectedLoadouts.TryGetValue(entry.Group.ID, out var choices) && choices.Any(x => x.Prototype.Id == item.ID);
                var selected = stored && !_entities.System<Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutSystem>().HasUnequippedSlots(_profile, _job, item);
                var replacementProfile = _entities.System<Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutSystem>().RemovePersonalConflicts(_profile, item);
                var valid = role.IsValid(replacementProfile, _session, item.ID, IoCManager.Instance!, out var reason);
                if (!_showUnavailable.Pressed && !valid && !selected)
                    continue;
                var title = JobItemName(item);
                var groupTitle = Loc.GetString(entry.Group.Name);
                var button = new Button { ToggleMode = true, Pressed = selected, Disabled = !valid && !selected, SetWidth = 156, MinHeight = 124, ToolTip = title + "\n" + groupTitle + "\n" + Loc.GetString("loadouts-min-limit", ("count", entry.Group.MinLimit)) + " / " + Loc.GetString("loadouts-max-limit", ("count", entry.Group.MaxLimit)) };
                if (!valid && reason != null)
                {
                    var tooltip = new Tooltip();
                    tooltip.SetMessage(reason);
                    button.TooltipSupplier = _ => tooltip;
                }
                var body = new BoxContainer { Orientation = LayoutOrientation.Vertical, Margin = new Thickness(4), MouseFilter = MouseFilterMode.Ignore };
                var entity = item.PreviewEntity ?? item.DummyEntity ?? _entities.System<LoadoutSystem>().GetFirstOrNull(item);
                if (entity is { } preview)
                    body.AddChild(new TextureRect { Texture = _entities.System<SpriteSystem>().GetPrototypeIcon(preview).Default, SetSize = new Vector2(48, 48), HorizontalAlignment = HAlignment.Center, Stretch = TextureRect.StretchMode.KeepAspectCentered, MouseFilter = MouseFilterMode.Ignore });
                body.AddChild(new RichTextLabel { Text = title, MaxWidth = 140, MinHeight = 36, MouseFilter = MouseFilterMode.Ignore });
                body.AddChild(new Label { Text = BankSystemExtensions.ToSpesoString(item.Price), MouseFilter = MouseFilterMode.Ignore });
                button.AddChild(body);
                button.OnPressed += _ =>
                {
                    if (button.Pressed)
                    {
                        if (!stored) role.AddLoadout(entry.Group.ID, item.ID, _prototypes);
                    }
                    else
                        role.RemoveLoadout(entry.Group.ID, item.ID, _prototypes);
                    var updated = (button.Pressed ? replacementProfile : _profile).WithLoadout(role);
                    if (button.Pressed)
                        updated = _entities.System<Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutSystem>().RestoreEquipmentSlots(updated, _job, item);
                    ProfileChanged?.Invoke(updated);
                };
                grid.AddChild(button);
                if (selected)
                    button.SetPositionInParent(selectedCount++);
        }
        return selectedCount;
    }

    private long JobCost(RoleLoadout role)
    {
        var system = _entities.System<PersonalLoadoutSystem>();
        return role.SelectedLoadouts.Values.SelectMany(items => items).Sum(choice =>
            _prototypes.TryIndex(choice.Prototype, out var item) && !system.FullyUnequipped(item, role.UnequippedSlots)
                ? (long)Math.Max(0, item.Price)
                : 0);
    }

    private bool CanAffordJob(RoleLoadout role, LoadoutGroupPrototype group, LoadoutPrototype item)
    {
        if (group.MaxLimit < 1 || !role.SelectedLoadouts.TryGetValue(group.ID, out var choices))
            return false;
        if (item.Price <= 0)
            return true;

        var candidate = role.Clone();
        if (choices.All(choice => choice.Prototype.Id != item.ID))
            candidate.AddLoadout(group.ID, item.ID, _prototypes);

        var system = _entities.System<PersonalLoadoutSystem>();
        if (candidate.UnequippedSlots.Count > 0)
            candidate.UnequippedSlots.ExceptWith(system.EditableSlots(_profile!).Where(slot => system.UsesSlot(item, slot)).Select(slot => slot.Name));
        return JobCost(candidate) <= _profile!.BankBalance;
    }

    private string JobCategory(LoadoutGroupPrototype group, LoadoutPrototype item)
    {
        _prototypes.TryIndex(item.StartingGear, out var gear);
        if (GearHasWeapon(item) || gear != null && GearHasWeapon(gear))
            return "Weapons";
        if (group.Name.Id is "loadout-group-trinkets" or "loadout-group-contractor-trinkets")
            return "Trinkets";

        return (item.Equipment.Keys.FirstOrDefault() ?? gear?.Equipment.Keys.FirstOrDefault()) switch
        {
            "jumpsuit" => "Uniform",
            "outerclothing" => "Outer",
            "shoes" => "Shoes",
            "gloves" => "Hands",
            "head" or "ears" or "helmetcover" or "helmetattachment" => "Head",
            "eyes" => "Eyes",
            "mask" or "balaclava" => "Mask",
            "neck" or "armbandright" or "armbandleft" => "Neck",
            "back" => "Backpacks",
            "belt" => "Belt",
            _ => "Items",
        };
    }

    private bool GearHasWeapon(IEquipmentLoadout gear)
    {
        return gear.Inhand.Any(IsWeapon) || gear.Storage.Values.Any(items => items?.Any(IsWeapon) == true) ||
               gear.Equipment.Values.Any(IsWeapon);
    }

    private string PersonalCategory(LoadoutPrototype item)
    {
        return item.PersonalCategory == "JobsSecurityWeapons" || item.PersonalItems.Any(IsWeapon)
            ? "Weapons"
            : item.PersonalCategory;
    }

    private bool IsWeapon(EntProtoId id)
    {
        if (!_prototypes.TryIndex(id, out var entity))
            return false;

        if (entity.TryGetComponent<TagComponent>(out var tags) && _entities.System<TagSystem>().HasTag(tags, "ToySidearm"))
            return false;

        if (entity.TryGetComponent<GunComponent>(out _) || entity.TryGetComponent<WeaponCaseInsertableComponent>(out _) ||
            entity.TryGetComponent<MeleeWeaponComponent>(out var melee) && !melee.Hidden && !entity.TryGetComponent<ToolComponent>(out _))
            return true;

        if (!entity.TryGetComponent<StorageComponent>(out _))
            return false;

        var parents = _prototypes.EnumerateAllParents<EntityPrototype>(entity.ID, includeSelf: true).Select(parent => parent.id).ToHashSet();
        return parents.Contains("WeaponCaseLong") && !parents.Contains("WeaponCaseShortDocuments");
    }

    private string JobItemName(LoadoutPrototype item) => string.IsNullOrWhiteSpace(item.Name) ? _entities.System<LoadoutSystem>().GetName(item) : item.Name;

    private bool FitsJobSlot(LoadoutPrototype item, Content.Shared.Inventory.SlotDefinition slot)
    {
        if (item.Equipment.ContainsKey(slot.Name))
            return true;
        return _prototypes.TryIndex(item.StartingGear, out var gear) && gear.Equipment.ContainsKey(slot.Name);
    }
}
