using System.Numerics;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Hands.Components;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client._DeepLagoon.Mapping;

/// <summary>
/// Placement ghost of the mapper RCD. Snaps to the tile under the cursor of the nearest grid and
/// only reports whether the tile is within reach, since the tool ignores obstructions.
/// </summary>
public sealed partial class AlignMapperRcd : PlacementMode
{
    [Dependency] private IEntityManager _entityManager = default!;
    private SharedMapSystem _mapManager => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private IPlayerManager _playerManager = default!;
    private readonly SharedMapSystem _mapSystem;
    private readonly SharedTransformSystem _transformSystem;

    private const float SearchBoxSize = 2f;
    private const float PlaceColorBaseAlpha = 0.5f;

    public AlignMapperRcd(PlacementManager pMan) : base(pMan)
    {
        IoCManager.InjectDependencies(this);
        _mapSystem = _entityManager.System<SharedMapSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        ValidPlaceColor = ValidPlaceColor.WithAlpha(PlaceColorBaseAlpha);
    }

    public override void AlignPlacementMode(ScreenCoordinates mouseScreen)
    {
        MouseCoords = ScreenToCursorGrid(mouseScreen).AlignWithClosestGridTile(SearchBoxSize, _entityManager);

        var gridId = _transformSystem.GetGrid(MouseCoords);

        if (!_entityManager.TryGetComponent<MapGridComponent>(gridId, out var mapGrid))
            return;

        CurrentTile = _mapSystem.GetTileRef(gridId.Value, mapGrid, MouseCoords);

        float tileSize = mapGrid.TileSize;
        GridDistancing = tileSize;

        var offset = pManager.CurrentPermission!.IsTile ? Vector2.Zero : (Vector2) pManager.PlacementOffset;
        MouseCoords = new EntityCoordinates(MouseCoords.EntityId,
            new Vector2(CurrentTile.X + tileSize / 2 + offset.X, CurrentTile.Y + tileSize / 2 + offset.Y));
    }

    public override bool IsValidPosition(EntityCoordinates position)
    {
        var player = _playerManager.LocalSession?.AttachedEntity;

        if (!_entityManager.TryGetComponent<TransformComponent>(player, out var xform) ||
            !_entityManager.TryGetComponent<HandsComponent>(player, out var hands) ||
            !_entityManager.TryGetComponent<MapperRcdComponent>(hands.ActiveHand?.HeldEntity, out var rcd))
        {
            return false;
        }

        var inRange = _transformSystem.InRange(xform.Coordinates, position, rcd.Range);
        InvalidPlaceColor = InvalidPlaceColor.WithAlpha(inRange ? PlaceColorBaseAlpha : 0);
        return inRange;
    }
}
