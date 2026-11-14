using System.Numerics;
using Content.Client.Atmos;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Placement;
using Robust.Client.Placement.Modes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Reflection;
using static Robust.Client.Placement.PlacementManager;

namespace Content.Client.Construction;

[Reflect(false)]
internal sealed class ConstructionPlacementMode : PlacementMode
{
    private const float PipeSnapDistance = 1f;

    private readonly PlacementMode _recipeMode;
    private readonly ConstructionPlacementHijack _hijack;
    private readonly AlignAtmosPipeLayers? _pipeMode;
    private readonly SharedMapSystem _maps;
    private readonly TransformSystem _transforms;
    private readonly SpriteSystem _sprites;
    private EntityCoordinates _cursorLocal;

    public override bool HasLineMode => _recipeMode.HasLineMode;
    public override bool HasGridMode => _recipeMode.HasGridMode;
    public override bool RangeRequired => _recipeMode.RangeRequired;

    public ConstructionPlacementMode(PlacementManager manager, PlacementMode recipeMode, ConstructionPlacementHijack hijack)
        : base(manager)
    {
        _recipeMode = recipeMode;
        _hijack = hijack;
        _pipeMode = recipeMode as AlignAtmosPipeLayers;
        _maps = manager.EntityManager.System<SharedMapSystem>();
        _transforms = manager.EntityManager.System<TransformSystem>();
        _sprites = manager.EntityManager.System<SpriteSystem>();
    }

    public override void AlignPlacementMode(ScreenCoordinates mouseScreen)
    {
        // The placement manager calls this when setting the anchor for a line or rectangle.
        Align(mouseScreen, PlacementTypes.None);
    }

    public override bool IsValidPosition(EntityCoordinates position) => _recipeMode.IsValidPosition(position);

    public void UpdatePreview(ScreenCoordinates mouseScreen)
    {
        _cursorLocal = Align(mouseScreen, pManager.PlacementType);
        if (_pipeMode != null && pManager.PlacementType == PlacementTypes.None &&
            pManager.EntityManager.HasComponent<MapGridComponent>(_cursorLocal.EntityId))
        {
            var rotation = _transforms.GetRenderWorldRotation(_cursorLocal.EntityId);
            var layer = AlignAtmosPipeLayers.GetLayer(_cursorLocal.Position - MouseCoords.Position,
                pManager.EyeManager.CurrentEye.Rotation + rotation);
            if (_pipeMode.TryGetConstructionLayer(_hijack.CurrentPrototype, layer, out var prototype))
                _hijack.SetPrototype(prototype);
        }
    }

    public void Place(ScreenCoordinates mouseScreen)
    {
        UpdatePreview(mouseScreen);
        foreach (var coordinates in GetPlacementCoordinates())
        {
            if (IsValidPosition(coordinates))
                _hijack.HijackPlacementRequest(coordinates);
        }

        pManager.PlacementType = PlacementTypes.None;
    }

    private EntityCoordinates Align(ScreenCoordinates mouseScreen, PlacementTypes placementType)
    {
        MouseCoords = EntityCoordinates.Invalid;
        var cursor = pManager.EyeManager.PixelToMap(mouseScreen);
        if (cursor.MapId == MapId.Nullspace || !_maps.TryGetMap(cursor.MapId, out var map))
            return MouseCoords;

        EntityCoordinates local;
        if (placementType == PlacementTypes.None)
        {
            local = FindGridAtCursor(cursor, map.Value);
        }
        else
        {
            var anchor = pManager.StartPoint;
            if (!anchor.IsValid(pManager.EntityManager) || _transforms.GetMapId(anchor) != cursor.MapId)
                return MouseCoords;

            local = new EntityCoordinates(anchor.EntityId,
                Vector2.Transform(cursor.Position, _transforms.GetInvRenderWorldMatrix(anchor.EntityId)));
        }

        MouseCoords = local;
        if (_recipeMode is not SnapgridCenter)
            return local;

        if (pManager.EntityManager.TryGetComponent(local.EntityId, out MapGridComponent? grid))
        {
            GridDistancing = grid.TileSize;
            MouseCoords = _maps.GridTileToLocal(local.EntityId, grid,
                _maps.CoordinatesToTile(local.EntityId, grid, local));
        }
        else
        {
            GridDistancing = 1f;
            MouseCoords = local.WithPosition(local.Position.Floored() + new Vector2(0.5f));
        }

        MouseCoords = MouseCoords.Offset(pManager.PlacementOffset);
        return local;
    }

    private EntityCoordinates FindGridAtCursor(MapCoordinates cursor, EntityUid map)
    {
        // The map's spatial index uses simulation bounds. A rotating grid can visually extend beyond them.
        var closest = new EntityCoordinates(map,
            Vector2.Transform(cursor.Position, _transforms.GetInvRenderWorldMatrix(map)));
        var closestDistance = float.PositiveInfinity;
        foreach (var grid in _maps.GetAllGrids(cursor.MapId))
        {
            if (grid.Owner == map)
                continue;

            var local = Vector2.Transform(cursor.Position, _transforms.GetInvRenderWorldMatrix(grid.Owner));
            var coordinates = new EntityCoordinates(grid.Owner, local);
            if (grid.Comp.LocalAABB.Contains(local) && !_maps.GetTileRef(grid, coordinates).Tile.IsEmpty)
                return coordinates;

            // Pipe placement also snaps near the edge of a grid.
            if (_pipeMode == null || !grid.Comp.LocalAABB.Enlarged(PipeSnapDistance).Contains(local))
                continue;

            var search = Box2.CenteredAround(local, new Vector2(2f * PipeSnapDistance));
            var tiles = _maps.GetLocalTilesIntersecting(grid.Owner, grid.Comp, search);
            while (tiles.MoveNext(out var tile))
            {
                var origin = _maps.TileToVector(grid, tile.GridIndices);
                var bounds = new Box2(origin, origin + new Vector2(grid.Comp.TileSize));
                var distance = Vector2.DistanceSquared(local, bounds.ClosestPoint(local));
                if (distance >= closestDistance)
                    continue;

                closestDistance = distance;
                closest = coordinates;
            }
        }

        return closest;
    }

    private IEnumerable<EntityCoordinates> GetPlacementCoordinates()
    {
        if (!MouseCoords.IsValid(pManager.EntityManager))
            yield break;

        if (pManager.PlacementType == PlacementTypes.None)
        {
            yield return MouseCoords;
            yield break;
        }

        var start = pManager.StartPoint;
        var size = (MouseCoords.Position - start.Position) / GridDistancing;
        var width = (int) MathF.Round(size.X);
        var height = (int) MathF.Round(size.Y);
        if (pManager.PlacementType == PlacementTypes.Line)
        {
            var offset = _cursorLocal.Position - start.Position;
            if (MathF.Abs(offset.X) > MathF.Abs(offset.Y))
                height = 0;
            else
                width = 0;
        }

        var xStep = Math.Sign(width) * GridDistancing;
        var yStep = Math.Sign(height) * GridDistancing;
        for (var x = 0; x <= Math.Abs(width); x++)
        {
            for (var y = 0; y <= Math.Abs(height); y++)
                yield return start.Offset(new Vector2(x * xStep, y * yStep));
        }
    }

    public override void Render(in OverlayDrawArgs args)
    {
        var entities = pManager.EntityManager;
        if (!MouseCoords.IsValid(entities) || _transforms.GetMapId(MouseCoords) != args.MapId ||
            pManager.CurrentPlacementOverlayEntity is not { } uid ||
            !entities.TryGetComponent(uid, out SpriteComponent? sprite) || !sprite.Visible)
            return;

        var (position, rotation) = _transforms.GetRenderWorldPositionRotation(MouseCoords.EntityId);
        var eyeRotation = args.Viewport.Eye?.Rotation ?? Angle.Zero;
        if (_recipeMode is SnapgridCenter && entities.HasComponent<MapGridComponent>(MouseCoords.EntityId))
        {
            DrawGrid(args, position, rotation);
            if (_pipeMode != null && pManager.PlacementType == PlacementTypes.None)
                _pipeMode.DrawLayerGuides(args, position + rotation.RotateVec(MouseCoords.Position), rotation);
        }

        foreach (var coordinates in GetPlacementCoordinates())
        {
            sprite.Color = IsValidPosition(coordinates) ? ValidPlaceColor : InvalidPlaceColor;
            _sprites.RenderSprite((uid, sprite), args.WorldHandle, eyeRotation, rotation + pManager.Direction.ToAngle(),
                position + rotation.RotateVec(coordinates.Position));
        }
    }

    private void DrawGrid(in OverlayDrawArgs args, Vector2 position, Angle rotation)
    {
        var world = Matrix3Helpers.CreateTransform(position, rotation);
        var inverse = Matrix3Helpers.CreateInverseTransform(position, rotation);
        var bounds = inverse.TransformBox(args.WorldBounds);
        var handle = args.WorldHandle;
        var color = new Color(0, 0, 0.3f);

        handle.SetTransform(world);
        for (var x = MathF.Ceiling(bounds.Left / GridDistancing) * GridDistancing; x < bounds.Right; x += GridDistancing)
            handle.DrawLine(new Vector2(x, bounds.Bottom), new Vector2(x, bounds.Top), color);

        for (var y = MathF.Ceiling(bounds.Bottom / GridDistancing) * GridDistancing; y < bounds.Top; y += GridDistancing)
            handle.DrawLine(new Vector2(bounds.Left, y), new Vector2(bounds.Right, y), color);

        handle.SetTransform(Matrix3x2.Identity);
    }
}
