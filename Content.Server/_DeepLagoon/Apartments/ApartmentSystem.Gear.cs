using System.Linq;
using Content.Shared.Storage;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    [Dependency] private MetaDataSystem _gearMetadata = default!;
    private readonly HashSet<EntityUid> _escapedChameleonGear = new();
    [Dependency] private Content.Server.Clothing.Systems.ChameleonClothingSystem _apartmentChameleon = default!;

    private void IssueChameleonGear(Visit visit)
    {
        // Use the uplink product itself so the apartment kit stays in sync with it.
        var backpack = Spawn("ClothingBackpackChameleonFill", Transform(visit.Body).Coordinates);
        visit.TemporaryGear.Add(backpack);
        foreach (var item in ApartmentEntities(backpack))
            visit.TemporaryGear.Add(item);
        foreach (var item in visit.TemporaryGear)
        {
            var gear = EnsureComp<ApartmentChameleonGearComponent>(item);
            gear.Grid = visit.Apartment.Grid;
            gear.ReturnCoordinates = Transform(visit.Body).Coordinates;
            _gearMetadata.AddFlag(item, MetaDataFlags.ExtraTransformEvents);
        }

        var storage = Comp<StorageComponent>(backpack).Container;
        foreach (var (prototype, slot) in new[]
        {
            ("ClothingUniformJumpsuitChameleon", "jumpsuit"), ("ClothingShoesChameleon", "shoes"),
            ("ClothingHeadsetChameleon", "ears"), ("ChameleonPDA", "id"),
            ("ClothingHandsChameleon", "gloves"), ("ClothingOuterChameleon", "outerClothing"),
            ("ClothingNeckChameleon", "neck"), ("ClothingMaskGasChameleon", "mask"),
            ("ClothingHeadHatChameleon", "head"), ("ClothingEyesChameleon", "eyes"),
        })
        {
            if (!visit.ClothingAppearances.TryGetValue(slot, out var appearance))
                continue;
            var item = storage.ContainedEntities.Single(uid => MetaData(uid).EntityPrototype?.ID == prototype);
            _apartmentChameleon.CopyAppearance(item, appearance);
        }
        if (visit.ClothingAppearances.TryGetValue("back", out var backpackAppearance))
            _apartmentChameleon.CopyAppearance(backpack, backpackAppearance);
        foreach (var (prototype, slot) in new[]
        {
            ("ClothingUniformJumpsuitChameleon", "jumpsuit"),
            ("ClothingShoesChameleon", "shoes"),
            ("ClothingHeadsetChameleon", "ears"),
            ("ChameleonPDA", "id"),
            ("ClothingHandsChameleon", "gloves"),
        })
        {
            var item = storage.ContainedEntities.Single(uid => MetaData(uid).EntityPrototype?.ID == prototype);
            if (!_inventory.HasSlot(visit.Body, slot))
                continue;
            _containers.Remove(item, storage);
            if (!_inventory.TryEquip(visit.Body, item, slot, silent: true, force: true))
                _containers.Insert(item, storage);
        }
        if (_inventory.HasSlot(visit.Body, "back"))
            _inventory.TryEquip(visit.Body, backpack, "back", silent: true, force: true);
    }

    private void OnChameleonGridChanged(Entity<ApartmentChameleonGearComponent> item, ref GridUidChangedEvent args)
    {
        if (args.NewGrid != item.Comp.Grid)
            _escapedChameleonGear.Add(item);
        // Grid changes propagate through every descendant, including closed containers
        // and carried bodies. Do not mutate the transform tree inside this event.
    }

    private void RemoveEscapedChameleonGear()
    {
        foreach (var item in _escapedChameleonGear.ToArray())
            DeleteChameleonGear(item);
        _escapedChameleonGear.Clear();
    }

    private void RemoveChameleonGear(Visit visit)
    {
        // Include items dropped on the floor or handed to another visitor.
        foreach (var item in visit.TemporaryGear)
            DeleteChameleonGear(item);
        visit.TemporaryGear.Clear();
    }

    private void DeleteChameleonGear(EntityUid item)
    {
        if (!Exists(item) || !TryComp<ApartmentChameleonGearComponent>(item, out var gear))
            return;

        // Keep purchases and other non-kit items placed in the backpack/PDA.
        // Move them back inside the original apartment before deleting their container.
        foreach (var child in ApartmentEntities(item).ToArray())
        {
            if (!Exists(child) || HasComp<ApartmentChameleonGearComponent>(child) || !HasComp<ApartmentChameleonGearComponent>(Transform(child).ParentUid) || !Exists(gear.Grid))
                continue;
            var destination = gear.ReturnCoordinates;
            if (_containers.TryGetContainingContainer(child, out var container))
                _containers.Remove(child, container, force: true, destination: destination);
            else
                _transform.SetCoordinates(child, destination);
        }
        Del(item);
    }
}
