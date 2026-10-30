using System.Linq;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Robust.Shared.GameStates;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Loadouts;

[RegisterComponent, NetworkedComponent]
public sealed partial class PersonalLoadoutVisualsComponent : Component;

[Serializable, NetSerializable]
public enum PersonalLoadoutVisuals : byte { Color, Heirloom }

/// <summary>Apply the same outfit on the preview dummy and the actual character.</summary>
public sealed partial class PersonalLoadoutEquipSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private PersonalLoadoutSystem _loadouts = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private ToggleableClothingSystem _toggleable = default!;

    public void Customize(EntityUid item, PersonalLoadoutCustomization? data)
    {
        if (data?.Name != null) _metadata.SetEntityName(item, data.Name);
        if (data?.Description != null) _metadata.SetEntityDescription(item, data.Description);
        if (data?.Color != null && Color.TryFromHex(data.Color, out var color))
            PaintOutfit(item, color, new HashSet<EntityUid>());
    }

    private void PaintOutfit(EntityUid item, Color color, HashSet<EntityUid> painted)
    {
        if (!painted.Add(item))
            return;

        EnsureComp<PersonalLoadoutVisualsComponent>(item);
        EnsureComp<AppearanceComponent>(item);
        _appearance.SetData(item, PersonalLoadoutVisuals.Color, color);

        // Attached helmets, hoods and MOD parts are separate entities, even when deployed.
        // Follow the clothing links rather than containers, which may hold unrelated items.
        if (_toggleable.GetAttachedClothingsList(item) is { } parts)
        {
            foreach (var part in parts)
                PaintOutfit(part, color, painted);
        }
    }

    public List<EntityUid> Apply(EntityUid character, HumanoidCharacterProfile profile, string job, ICommonSession? session, bool preview = false)
    {
        var failed = new List<EntityUid>();
        if (!profile.Loadouts.TryGetValue(PersonalLoadoutSystem.Role, out var saved) || !_inventory.TryGetSlots(character, out var slots))
            return failed;
        var role = saved.Clone();
        role.EnsureValid(profile, session, IoCManager.Instance!);
        var unequipped = _loadouts.UnequippedSlots(profile, job);
        var remaining = _loadouts.GetPoints(session);
        foreach (var selected in role.SelectedLoadouts.OrderBy(x => x.Key.Id).SelectMany(x => x.Value))
        {
            if (!_prototypes.TryIndex(selected.Prototype, out var prototype) || prototype.PersonalItems.Count == 0 || !_loadouts.CanUse(prototype, profile, job, session, out _))
                continue;
            var cost = Math.Max(0, prototype.PersonalCost);
            if (remaining < cost)
                continue;
            remaining -= cost;
            foreach (var itemPrototype in prototype.PersonalItems)
            {
                var data = _loadouts.Sanitize(prototype, selected.Customization);
                var targetSlot = data?.Slot ?? _loadouts.PersonalSlot(profile, prototype);
                var targetSlots = slots.Where(slot => targetSlot == null || slot.Name == targetSlot).ToArray();
                // A mandatory personal choice may be restored by validation.
                // Do not spawn it into another slot or drop it as a failed item.
                if (_prototypes.Index(itemPrototype).TryGetComponent<ClothingComponent>(out var clothing) &&
                    targetSlots.Any(slot => unequipped.Contains(slot.Name) && (clothing.Slots & slot.SlotFlags) != 0))
                    continue;
                var item = Spawn(itemPrototype, Transform(character).Coordinates);
                Customize(item, data);
                if (data?.Heirloom == true)
                {
                    EnsureComp<PersonalLoadoutVisualsComponent>(item);
                    EnsureComp<AppearanceComponent>(item);
                    _appearance.SetData(item, PersonalLoadoutVisuals.Heirloom, true);
                }
                var equipped = false;
                if (HasComp<ClothingComponent>(item))
                {
                    foreach (var slot in targetSlots)
                    {
                        if (unequipped.Contains(slot.Name)) continue;
                        // Never substitute clothes into pockets or suit storage.
                        if ((slot.SlotFlags & (SlotFlags.POCKET | SlotFlags.SUITSTORAGE)) != 0)
                            continue;
                        if (!_inventory.CanEquip(character, item, slot.Name, out _))
                            continue;
                        if (_inventory.TryGetSlotEntity(character, slot.Name, out var previous))
                        {
                            if (!prototype.PersonalExclusive && data?.Slot == null)
                                continue;
                            if (!_inventory.TryUnequip(character, slot.Name, silent: true, force: true))
                                continue;
                            Del(previous);
                        }
                        equipped = _inventory.TryEquip(character, item, slot.Name, silent: true, force: true);
                        if (equipped)
                            break;
                    }
                }
                if (!equipped)
                {
                    if (preview)
                        Del(item);
                    else
                        failed.Add(item);
                }
            }
        }
        return failed;
    }
}
