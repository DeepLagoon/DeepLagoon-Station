using System.Linq;
using Content.Shared.Item;
using Content.Shared.Storage.Components;
using Robust.Shared.Utility;

namespace Content.Shared.Storage.EntitySystems;

public abstract partial class SharedStorageSystem
{
    /// <summary>Shows fixed prototype contents in a local loadout preview. Chance-based loot is rolled on the server.</summary>
    public void FillLoadoutPreview(Entity<StorageComponent> box)
    {
        if (box.Comp.Container.Count != 0 || !TryComp<StorageFillComponent>(box, out var fill))
            return;

        var items = new List<Entity<ItemComponent>>();
        foreach (var entry in fill.Contents)
        {
            if (entry.PrototypeId is not { } prototype || entry.GroupId != null ||
                entry.SpawnProbability != 1 || entry.MaxAmount > entry.Amount)
                continue;
            for (var i = 0; i < entry.Amount; i++)
            {
                var item = Spawn(prototype, Transform(box).Coordinates);
                if (TryComp<ItemComponent>(item, out var component))
                    items.Add((item, component));
                else
                    Del(item);
            }
        }
        foreach (var item in items.OrderByDescending(item => ItemSystem.GetItemShape(item.Comp).GetArea()))
            if (!Insert(box, item, out _, storageComp: box.Comp, playSound: false))
                Del(item);
    }
}
