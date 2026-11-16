using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Hands.Components;
using Content.Shared.Item;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Lobby.UI.Loadouts;

public sealed partial class PersonalLoadoutEditor
{
    private string? _handFilter;

    private sealed record HandChoice(StartingGearHandItem Item, LoadoutPrototype? Loadout,
        ProtoId<LoadoutGroupPrototype>? Group, bool Personal = false);

    public bool SelectTguiHand(string name, bool toggle)
    {
        if (_profile == null || !_entities.System<PersonalLoadoutSystem>().StartingHands(_profile).Any(hand => hand.Name == name))
            return false;

        _handFilter = toggle && _handFilter == name ? null : name;
        _slotFilter = null;
        _pendingSlotCategory = null;
        _cachedTguiState = null;
        UpdateSlotHighlights();
        SlotOpened?.Invoke();
        return true;
    }

    public IEnumerable<TguiData> CreateTguiHands()
    {
        if (_profile == null)
            yield break;

        var hands = _entities.System<PersonalLoadoutSystem>().StartingHands(_profile).ToArray();
        if (hands.Length == 0)
            yield break;

        var placed = _jobRole == null
            ? new Dictionary<string, HandChoice>()
            : PreviewHands(_jobRole, GetRole());
        foreach (var hand in hands.OrderBy(hand => hand.Location))
        {
            var held = placed.GetValueOrDefault(hand.Name);
            var texture = hand.Location switch
            {
                HandLocation.Left => "hand_l",
                HandLocation.Right => "hand_r",
                _ => "hand_m",
            };
            yield return new TguiData().String("id", hand.Name).String("name", HandLabel(hand.Location))
                .String("background", ThemeImage("Slots/" + texture + ".png")).Bool("selected", hand.Name == _handFilter)
                .String("iconId", held == null ? null : "item/" + held.Item.Prototype.Id)
                .String("item", held == null ? null : held.Item.Customization?.Name ?? _prototypes.Index(held.Item.Prototype).Name)
                .String("color", held?.Item.Customization?.Color);
        }
    }

    private string ThemeImage(string suffix)
    {
        var path = Theme.Path.ToString();
        var image = path.StartsWith("/Textures/", StringComparison.Ordinal)
            ? _images.Frame(new SpriteSpecifier.Texture(new ResPath(path["/Textures/".Length..] + "/" + suffix)))
            : "";
        return image.Length > 0 ? image : _images.Frame(new SpriteSpecifier.Texture(new ResPath("Interface/Default/" + suffix)));
    }

    private static string HandLabel(HandLocation side) => side switch
    {
        HandLocation.Left => Loc.GetString("dl-loadout-left-hand"),
        HandLocation.Right => Loc.GetString("dl-loadout-right-hand"),
        _ => Loc.GetString("dl-loadout-middle-hand"),
    };

    private IEnumerable<HandChoice> JobHandChoices(LoadoutPrototype item, ProtoId<LoadoutGroupPrototype> group, PersonalLoadoutCustomization? custom = null)
    {
        if (_prototypes.TryIndex(item.StartingGear, out var gear))
            foreach (var held in SharedStationSpawningSystem.GetStartingGearHandItems(gear, custom))
                yield return new HandChoice(held, item, group);
        foreach (var held in SharedStationSpawningSystem.GetStartingGearHandItems(item, custom))
            yield return new HandChoice(held, item, group);
    }

    private IEnumerable<HandChoice> PersonalHandChoices(LoadoutPrototype item, ProtoId<LoadoutGroupPrototype> group, PersonalLoadoutCustomization? custom = null)
    {
        if (item.PersonalInhand)
            for (var i = 0; i < item.PersonalItems.Count; i++)
                yield return new HandChoice(PersonalLoadoutEquipSystem.GetPersonalHandItem(item, i, custom), item, group, true);
    }

    private IEnumerable<HandChoice> SelectedHandChoices(RoleLoadout role, RoleLoadout personal)
    {
        var groups = _prototypes.Index(role.Role).Groups;
        var balance = _profile!.BankBalance;
        foreach (var (group, choices) in role.SelectedLoadouts.OrderBy(entry => groups.IndexOf(entry.Key)))
        {
            foreach (var choice in choices)
            {
                var item = _prototypes.Index(choice.Prototype);
                if (item.Price > balance)
                    continue;
                balance -= Math.Max(0, item.Price);
                foreach (var held in JobHandChoices(item, group, choice.Customization))
                    yield return held;
            }
        }
        if (_prototypes.TryIndex<JobPrototype>(_job, out var job) && _prototypes.TryIndex(job.StartingGear, out var gear))
            foreach (var held in SharedStationSpawningSystem.GetStartingGearHandItems(gear))
                yield return new HandChoice(held, null, null);

        var system = _entities.System<PersonalLoadoutSystem>();
        var points = system.GetPoints(_session);
        foreach (var (group, choices) in personal.SelectedLoadouts.OrderBy(entry => entry.Key.Id))
        {
            foreach (var choice in choices)
            {
                var item = _prototypes.Index(choice.Prototype);
                var cost = Math.Max(0, item.PersonalCost);
                if (cost > points || !system.CanUse(item, _profile, _job, _session, out _))
                    continue;
                points -= cost;
                foreach (var held in PersonalHandChoices(item, group, choice.Customization))
                    yield return held;
            }
        }
    }

    private Dictionary<string, HandChoice> PreviewHands(RoleLoadout role, RoleLoadout personal)
    {
        var choices = SelectedHandChoices(role, personal).DistinctBy(choice => choice.Item.Key)
            .ToDictionary(choice => choice.Item.Key!);
        var hands = _entities.System<PersonalLoadoutSystem>().StartingHands(_profile!).Select(hand => hand.Name);
        return StartingGearHandItem.Plan(choices.Values.Select(choice => choice.Item), role.HandAssignments, hands)
            .Where(entry => entry.Hand != null).ToDictionary(entry => entry.Hand!, entry => choices[entry.Item.Key!]);
    }

    private IEnumerable<HandChoice> HandCatalogue(RoleLoadout role, RoleLoadout personal)
    {
        var system = _entities.System<PersonalLoadoutSystem>();
        var groups = _prototypes.Index(personal.Role).Groups;
        var choices = _jobEntries.SelectMany(entry => JobHandChoices(entry.Item, entry.Group.ID))
            .Concat(system.Catalogue(_profile!, _job, _session).Where(item => item.PersonalInhand)
                .SelectMany(item => groups.Where(group => _prototypes.Index(group).Loadouts.Contains(item.ID)).Take(1)
                    .SelectMany(group => PersonalHandChoices(item, group))));
        return SelectedHandChoices(role, personal).Concat(choices)
            .DistinctBy(choice => choice.Item.Key).Where(choice => _prototypes.Index(choice.Item.Prototype).TryGetComponent<ItemComponent>(out _));
    }

    private bool CanSelectHandChoice(HandChoice choice, RoleLoadout role, RoleLoadout personal, out string? reason)
    {
        reason = null;
        if (choice.Loadout is not { } loadout || choice.Group is not { } group)
            return true;

        var system = _entities.System<PersonalLoadoutSystem>();
        if (choice.Personal)
        {
            if (!system.CanUse(loadout, _profile!, _job, _session, out reason))
                return false;
            if (CanAffordPersonal(loadout, personal))
                return true;
            reason = Loc.GetString("dl-loadout-insufficient-points");
            return false;
        }

        if (!role.IsValid(system.RemovePersonalConflicts(_profile!, loadout), _session, loadout.ID, IoCManager.Instance!, out var failure))
        {
            reason = failure?.ToString();
            return false;
        }
        if (CanAffordJob(role, _prototypes.Index(group), loadout))
            return true;
        reason = Loc.GetString("dl-loadout-insufficient-balance");
        return false;
    }

    private IEnumerable<TguiData> CreateTguiHandChoices(RoleLoadout role, RoleLoadout personal)
    {
        var placed = PreviewHands(role, personal);
        placed.TryGetValue(_handFilter!, out var held);
        var options = HandCatalogue(role, personal);
        var budgetRole = role.Clone();
        var budgetPersonal = personal.Clone();
        if (held != null)
            RemoveHandChoice(held, budgetRole, budgetPersonal);
        foreach (var choice in options)
        {
            var selected = choice.Item.Key == held?.Item.Key;
            string? reason = null;
            var allowed = selected || CanSelectHandChoice(choice, budgetRole, budgetPersonal, out reason);
            var entity = _prototypes.Index(choice.Item.Prototype);
            var item = choice.Loadout;
            var name = item != null && !choice.Personal && IsSingleHandLoadout(item, false) ? JobItemName(item) : entity.Name;
            yield return new TguiData().String("id", item?.ID ?? entity.ID).String("handKey", choice.Item.Key)
                .String("name", name).String("customName", choice.Item.Customization?.Name).String("reason", reason)
                .String("iconId", "item/" + entity.ID).String("group", choice.Group?.Id)
                .Bool("jobItem", !choice.Personal).Bool("selected", selected).Bool("allowed", allowed)
                .Bool("canSelect", allowed)
                .Number("cost", item == null ? 0 : choice.Personal ? item.PersonalCost : item.Price)
                .Bool("canPaint", item != null).Bool("canRename", item != null)
                .String("color", choice.Item.Customization?.Color);
        }
    }

    private bool EquipTguiHand(string key)
    {
        if (_profile == null || _handFilter == null || _jobRole == null)
            return false;

        var role = _jobRole.Clone();
        var personal = GetRole();
        var choice = HandCatalogue(role, personal).FirstOrDefault(choice => choice.Item.Key == key);
        if (choice == null)
            return false;

        var placed = PreviewHands(role, personal);
        placed.TryGetValue(_handFilter, out var previous);
        if (previous != null)
            RemoveHandChoice(previous, role, personal);
        LoadoutPrototype? jobChoice = null;
        if (previous?.Item.Key != key)
        {
            if (!CanSelectHandChoice(choice, role, personal, out _) ||
                role.HandAssignments.Count >= 64 && !role.HandAssignments.ContainsKey(key))
                return false;
            if (choice.Loadout is { } loadout && choice.Group is { } group)
            {
                var selection = choice.Personal ? personal : role;
                if (!selection.SelectedLoadouts[group].Any(item => item.Prototype.Id == loadout.ID))
                    selection.AddLoadout(group, loadout.ID, _prototypes);
                if (!choice.Personal)
                    jobChoice = loadout;
            }
            foreach (var conflict in role.HandAssignments.Where(entry => entry.Value == _handFilter).Select(entry => entry.Key).ToArray())
                role.HandAssignments.Remove(conflict);
            role.HandAssignments[key] = _handFilter;
        }

        var profile = _profile.WithLoadout(personal).WithLoadout(role);
        if (jobChoice != null)
        {
            var system = _entities.System<PersonalLoadoutSystem>();
            profile = system.RemovePersonalConflicts(profile, jobChoice);
            profile = system.RestoreEquipmentSlots(profile, _job, jobChoice);
        }
        ProfileChanged?.Invoke(profile);
        return true;
    }

    private void RemoveHandChoice(HandChoice choice, RoleLoadout role, RoleLoadout personal)
    {
        var key = choice.Item.Key!;
        var selection = choice.Personal ? personal : role;
        if (choice.Loadout is { } loadout && choice.Group is { } group && IsSingleHandLoadout(loadout, choice.Personal) &&
            selection.SelectedLoadouts.TryGetValue(group, out var items) && items.Count > _prototypes.Index(group).MinLimit &&
            selection.RemoveLoadout(group, loadout.ID, _prototypes))
        {
            role.HandAssignments.Remove(key);
            return;
        }
        role.HandAssignments[key] = "";
    }

    private static bool IsSingleHandLoadout(LoadoutPrototype item, bool personal) => personal
        ? item.PersonalItems.Count == 1
        : item.Inhand.Count == 1 && item.StartingGear == null && item.Equipment.Count == 0 && item.Storage.Count == 0 && item.Components.Count == 0;
}
