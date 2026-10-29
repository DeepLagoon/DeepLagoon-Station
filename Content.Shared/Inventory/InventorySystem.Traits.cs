using System.Linq;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared.Inventory;

public partial class InventorySystem
{
    public void RefreshTraitTemplate(EntityUid uid, InventoryComponent inventory)
    {
        if (!_prototypeManager.TryIndex(inventory.TemplateId, out InventoryTemplatePrototype? template)) return;
        var names = template.Slots.Select(slot => slot.Name).ToHashSet();
        foreach (var container in inventory.Containers)
            if (!names.Contains(container.ID))
                foreach (var item in container.ContainedEntities.ToArray())
                    EntityManager.System<SharedTransformSystem>().AttachToGridOrMap(item);
        inventory.Slots = template.Slots;
        inventory.Containers = new ContainerSlot[inventory.Slots.Length];
        for (var i = 0; i < inventory.Slots.Length; i++)
        {
            var container = _containerSystem.EnsureContainer<ContainerSlot>(uid, inventory.Slots[i].Name);
            container.OccludesLight = false;
            inventory.Containers[i] = container;
        }
        Dirty(uid, inventory);
        var updated = new InventoryTemplateUpdated();
        RaiseLocalEvent(uid, ref updated);
    }
}
