using System.Linq;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared._Mono.ShipRepair;
namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private void RemoveRetiredEquipment(EntityUid grid)
    {
        var ship = Comp<ParkingShuttleComponent>(grid); var removed = 0;
        foreach (var entity in Descendants(grid).Skip(1).Reverse().ToArray())
        {
            if (TerminatingOrDeleted(entity) || Prototype(entity) is not { } prototype || !LaserDrillAvailability.IsRetiredPrototype(_prototypes, prototype.ID)) continue;
            var at = _transform.WithEntityId(Transform(entity).Coordinates, grid);
            // Preserve unrelated contents even in hand-edited legacy saves.
            var children = Transform(entity).ChildEnumerator; var retained = new List<EntityUid>();
            while (children.MoveNext(out var child)) retained.Add(child);
            foreach (var child in retained)
            { _containers.TryRemoveFromContainer(child, force: true); _transform.SetCoordinates(child, at); }
            if (TryComp<ParkingOriginalComponent>(entity, out var original)) ship.OriginalItems.Remove(original.Identity);
            Del(entity); removed++;
        }
        EntityManager.System<SharedShipRepairSystem>().RemoveRetiredRepairData(grid);
        if (removed > 0) Log.Info($"Removed {removed} retired laser drills/flatpacks from stored shuttle {ship.Identity}.");
    }
}
