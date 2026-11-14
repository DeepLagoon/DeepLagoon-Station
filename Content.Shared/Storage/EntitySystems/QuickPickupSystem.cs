using Content.Shared.Interaction;
using Content.Shared.Item;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;

namespace Content.Shared.Storage.EntitySystems;

public sealed partial class QuickPickupSystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedMapSystem _map = default!;

    /// <summary>
    /// Finds loose copies of the clicked item. Call before inserting the target, while it is still on the tile.
    /// </summary>
    public List<EntityUid> FindMatchingItems(EntityUid target, EntityUid user)
    {
        var items = new List<EntityUid>();
        if (MetaData(target).EntityPrototype is not { ID: var prototypeId } ||
            Transform(target) is not { Anchored: false, GridUid: { } gridUid } xform ||
            !TryComp<MapGridComponent>(gridUid, out var grid) || _container.IsEntityInContainer(target))
        {
            return items;
        }

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var flags = LookupFlags.Uncontained | LookupFlags.Approximate;
        foreach (var item in _lookup.GetLocalEntitiesIntersecting(gridUid, tile, enlargement: 0, flags: flags, gridComp: grid))
        {
            if (item == target || item == user || !HasComp<ItemComponent>(item) ||
                MetaData(item).EntityPrototype?.ID != prototypeId ||
                Transform(item) is not { Anchored: false } itemXform || itemXform.GridUid != gridUid ||
                _map.TileIndicesFor(gridUid, grid, itemXform.Coordinates) != tile ||
                _container.IsEntityInContainer(item) || !_interaction.InRangeAndAccessible(user, item))
            {
                continue;
            }

            items.Add(item);
        }

        // Prediction and the server must pick the same copies when there is limited room left.
        items.Sort((a, b) => GetNetEntity(a).CompareTo(GetNetEntity(b)));
        return items;
    }
}
