using System.Linq;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Mono.ShipRepair.Components;
namespace Content.Shared._Mono.ShipRepair;

public abstract partial class SharedShipRepairSystem
{
    public void RemoveRetiredRepairData(EntityUid grid)
    {
        if (!TryComp<ShipRepairDataComponent>(grid, out var data)) return;
        var removed = data.EntityPalette.Select((id, index) => (id, index))
            .Where(p => LaserDrillAvailability.IsRetiredPrototype(_proto, p.id)).Select(p => p.index).ToHashSet();
        if (removed.Count == 0) return;
        var map = new Dictionary<int, int>();
        for (var i = 0; i < data.EntityPalette.Count; i++) if (!removed.Contains(i)) map[i] = map.Count;
        foreach (var chunk in data.Chunks.Values)
            foreach (var (id, spec) in chunk.Entities.ToArray())
            {
                if (!map.TryGetValue(spec.ProtoIndex, out var index)) chunk.Entities.Remove(id);
                else spec.ProtoIndex = index;
            }
        data.EntityPalette = data.EntityPalette.Where((_, i) => !removed.Contains(i)).ToList();
        data.SnapshotVersion = Guid.NewGuid(); Dirty(grid, data);
    }
}
