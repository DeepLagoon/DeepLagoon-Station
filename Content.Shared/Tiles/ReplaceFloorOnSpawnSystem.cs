using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Shared.Tiles;

public sealed partial class ReplaceFloorOnSpawnSystem : EntitySystem
{
    [Dependency] private ITileDefinitionManager _tile = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IRobustRandom _random = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<ReplaceFloorOnSpawnComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<ReplaceFloorOnSpawnComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);
        if (xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return;

        if (ent.Comp.ReplaceableTiles != null && ent.Comp.ReplaceableTiles.Count == 0)
            return;

        var tileIndices = _map.LocalToTile(grid, gridComp, xform.Coordinates);

        foreach (var offset in ent.Comp.Offsets)
        {
            var actualIndices = tileIndices + offset;

            if (!_map.TryGetTileRef(grid, gridComp, actualIndices, out var tile))
                continue;

            if (ent.Comp.ReplaceableTiles != null &&
                !tile.Tile.IsEmpty &&
                !ent.Comp.ReplaceableTiles.Contains(_tile[tile.Tile.TypeId].ID))
                continue;

            var tileToSet = _random.Pick(ent.Comp.ReplacementTiles);
            _map.SetTile(grid, gridComp, tile.GridIndices, new Tile(_prototype.Index(tileToSet).TileId));
        }
    }
}
