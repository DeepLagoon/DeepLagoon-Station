using System.Linq;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Shared._DeepLagoon.Loadouts;

public sealed partial class PersonalLoadoutSystem
{
    public SlotDefinition[] EditableSlots(HumanoidCharacterProfile profile)
    {
        if (!_prototypes.TryIndex(profile.Species, out var species) ||
            !_prototypes.Index(species.Prototype).TryGetComponent<InventoryComponent>(out var inventory) ||
            !_prototypes.TryIndex<InventoryTemplatePrototype>(inventory.TemplateId, out var template))
            return Array.Empty<SlotDefinition>();
        return template.Slots.Where(slot =>
            (slot.SlotFlags & (SlotFlags.POCKET | SlotFlags.SUITSTORAGE | SlotFlags.PREVENTEQUIP)) == 0).ToArray();
    }

    public string EquipmentRole(string job) => _prototypes.HasIndex<RoleLoadoutPrototype>(LoadoutSystem.GetJobPrototype(job))
        ? LoadoutSystem.GetJobPrototype(job) : Role;

    public IReadOnlySet<string> UnequippedSlots(HumanoidCharacterProfile profile, string job) =>
        profile.Loadouts.TryGetValue(EquipmentRole(job), out var role) ? role.UnequippedSlots : new HashSet<string>();

    public bool UsesSlot(LoadoutPrototype item, SlotDefinition slot) =>
        item.Equipment.ContainsKey(slot.Name) ||
        _prototypes.TryIndex(item.StartingGear, out var gear) && gear.Equipment.ContainsKey(slot.Name) ||
        item.PersonalItems.Any(id => _prototypes.Index(id).TryGetComponent<ClothingComponent>(out var clothing) &&
            (clothing.Slots & slot.SlotFlags) != 0);

    public bool HasUnequippedSlots(HumanoidCharacterProfile profile, string job, LoadoutPrototype item)
    {
        var unequipped = UnequippedSlots(profile, job);
        return unequipped.Count > 0 && EditableSlots(profile).Any(slot => unequipped.Contains(slot.Name) && UsesSlot(item, slot));
    }

    public bool FullyUnequipped(LoadoutPrototype item, IReadOnlySet<string> unequipped)
    {
        if (unequipped.Count == 0 || item.Inhand.Count > 0 || item.Components.Count > 0) return false;
        var equipment = item.Equipment.Keys.AsEnumerable();
        if (_prototypes.TryIndex(item.StartingGear, out var gear))
        {
            if (gear.Inhand.Count > 0) return false;
            equipment = equipment.Concat(gear.Equipment.Keys);
        }
        var slots = equipment.ToArray();
        return slots.Length > 0 && slots.All(unequipped.Contains);
    }

    public HumanoidCharacterProfile RestoreEquipmentSlots(HumanoidCharacterProfile profile, string job, LoadoutPrototype item, string? targetSlot = null)
    {
        if (!profile.Loadouts.TryGetValue(EquipmentRole(job), out var stored) || stored.UnequippedSlots.Count == 0)
            return profile;
        var role = stored.Clone();
        role.UnequippedSlots.ExceptWith(EditableSlots(profile).Where(slot => (targetSlot == null || slot.Name == targetSlot) && UsesSlot(item, slot)).Select(slot => slot.Name));
        return profile.WithLoadout(role);
    }

    /// <summary>Compare actual equipment slots, not catalogue category labels.</summary>
    public bool Conflicts(HumanoidCharacterProfile profile, LoadoutPrototype personal, LoadoutPrototype jobItem, string? targetSlot = null)
    {
        if ((!personal.PersonalExclusive && targetSlot == null) || personal.PersonalItems.Count == 0)
            return false;
        var species = _prototypes.Index(profile.Species);
        if (!_prototypes.Index(species.Prototype).TryGetComponent<InventoryComponent>(out var inventory) ||
            !_prototypes.TryIndex<InventoryTemplatePrototype>(inventory.TemplateId, out var template))
            return false;
        var equipment = jobItem.Equipment.Keys.AsEnumerable();
        if (_prototypes.TryIndex(jobItem.StartingGear, out var gear))
            equipment = equipment.Concat(gear.Equipment.Keys);
        var slots = template.Slots.Where(slot => (targetSlot == null || slot.Name == targetSlot) && equipment.Contains(slot.Name) &&
            (slot.SlotFlags & (SlotFlags.POCKET | SlotFlags.SUITSTORAGE | SlotFlags.PREVENTEQUIP)) == 0);
        return slots.Any(slot => personal.PersonalItems.Any(id =>
            _prototypes.Index(id).TryGetComponent<ClothingComponent>(out var clothing) &&
            (clothing.Slots & slot.SlotFlags) != 0));
    }

    public HumanoidCharacterProfile RemovePersonalConflicts(HumanoidCharacterProfile profile, LoadoutPrototype jobItem)
    {
        if (!profile.Loadouts.TryGetValue(Role, out var stored))
            return profile;
        var personal = stored.Clone();
        var removed = 0;
        foreach (var group in personal.SelectedLoadouts.Values)
            removed += group.RemoveAll(item => _prototypes.TryIndex(item.Prototype, out var proto) && Conflicts(profile, proto, jobItem, item.Customization?.Slot));
        return removed == 0 ? profile : profile.WithLoadout(personal);
    }

    public RoleLoadout RemoveJobConflicts(HumanoidCharacterProfile profile, RoleLoadout stored, LoadoutPrototype personal, string? targetSlot = null)
    {
        var role = stored.Clone();
        foreach (var group in role.SelectedLoadouts.Values)
            group.RemoveAll(item => _prototypes.TryIndex(item.Prototype, out var proto) && Conflicts(profile, personal, proto, targetSlot));
        return role;
    }

    public string? PersonalSlot(HumanoidCharacterProfile profile, LoadoutPrototype item, string? requested = null)
    {
        var slots = EditableSlots(profile).Where(slot => UsesSlot(item, slot)).ToArray();
        if (requested != null) return slots.FirstOrDefault(slot => slot.Name == requested)?.Name;
        var preferred = item.PersonalCategory switch
        {
            "Hands" => "gloves", "Shoes" => "shoes", "Head" => "head", "Eyes" => "eyes",
            "Mask" => "mask", "Neck" => "neck", "Uniform" => "jumpsuit", "Outer" => "outerClothing",
            "Backpacks" => "back", "Belt" => "belt", _ => null,
        };
        return slots.FirstOrDefault(slot => slot.Name == preferred)?.Name ?? slots.FirstOrDefault()?.Name;
    }

    public IEnumerable<LoadoutPrototype> Catalogue(HumanoidCharacterProfile profile, string job, ICommonSession? session)
    {
        var selected = GetSelections(profile).Select(choice => choice.Prototype.Id).ToHashSet();
        return _prototypes.EnumeratePrototypes<LoadoutPrototype>().Where(item => item.PersonalItems.Count > 0)
            .GroupBy(item => (Items: string.Join("\n", item.PersonalItems), Slot: PersonalSlot(profile, item),
                item.PersonalCost, item.PersonalExclusive, item.PersonalDonor, item.PersonalDonorTier, item.PersonalHeirloom))
            .Select(group => group.OrderByDescending(item => CanUse(item, profile, job, session, out _))
                .ThenByDescending(item => selected.Contains(item.ID))
                .ThenBy(item => item.PersonalRequirements.Count).ThenBy(item => item.ID, StringComparer.Ordinal).First());
    }

    public void RemovePersonalSlotConflicts(RoleLoadout role, HumanoidCharacterProfile profile, LoadoutPrototype item, string? requested)
    {
        if ((!item.PersonalExclusive && requested == null) || PersonalSlot(profile, item, requested) is not { } slot) return;
        foreach (var choices in role.SelectedLoadouts.Values)
            choices.RemoveAll(choice => _prototypes.TryIndex(choice.Prototype, out var other) &&
                PersonalSlot(profile, other, choice.Customization?.Slot) == slot);
    }

    /// <summary>Prevent mandatory job defaults from restoring replaced clothing on validation/spawn.</summary>
    public bool IsOverridden(HumanoidCharacterProfile profile, ProtoId<RoleLoadoutPrototype> role, LoadoutPrototype item, ICommonSession? session)
    {
        if (role.Id == Role || !profile.Loadouts.TryGetValue(Role, out var personal))
            return false;
        var job = _prototypes.EnumeratePrototypes<JobPrototype>().FirstOrDefault(p => LoadoutSystem.GetJobPrototype(p.ID) == role.Id);
        if (job == null)
            return false;
        var valid = personal.Clone();
        valid.EnsureValid(profile, session, IoCManager.Instance!);
        return valid.SelectedLoadouts.Values.SelectMany(group => group).Any(selection =>
            _prototypes.TryIndex(selection.Prototype, out var proto) && CanUse(proto, profile, job.ID, session, out _) && Conflicts(profile, proto, item, selection.Customization?.Slot));
    }
}
