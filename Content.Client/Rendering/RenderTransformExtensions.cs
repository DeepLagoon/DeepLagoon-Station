using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client.Rendering;

/// <summary>Read one interpolated pose for drawing and its inverse for visual culling.</summary>
public static class RenderTransformExtensions
{
    public static SharedMapSystem.TilesEnumerator GetRenderTilesIntersecting(this SharedMapSystem maps,
        TransformSystem transforms, Entity<MapGridComponent> grid, Box2 bounds)
        => maps.GetLocalTilesIntersecting(grid.Owner, grid.Comp,
            transforms.GetInvRenderWorldMatrix(grid.Owner).TransformBox(bounds));

    public static SharedMapSystem.TilesEnumerator GetRenderTilesIntersecting(this SharedMapSystem maps,
        TransformSystem transforms, Entity<MapGridComponent> grid, Box2Rotated bounds)
        => maps.GetLocalTilesIntersecting(grid.Owner, grid.Comp,
            transforms.GetInvRenderWorldMatrix(grid.Owner).TransformBox(bounds));

    public static (Vector2 Position, Angle Rotation, Matrix3x2 Matrix) GetRenderWorldPositionRotationMatrix(
        this TransformSystem transforms, Entity<TransformComponent?> entity)
    {
        var (position, rotation) = transforms.GetRenderWorldPositionRotation(entity);
        return (position, rotation, Matrix3Helpers.CreateTransform(position, rotation));
    }

    public static (Vector2 Position, Angle Rotation, Matrix3x2 Matrix, Matrix3x2 Inverse)
        GetRenderWorldPositionRotationMatrixWithInv(this TransformSystem transforms, Entity<TransformComponent?> entity)
    {
        var (position, rotation, matrix) = transforms.GetRenderWorldPositionRotationMatrix(entity);
        return (position, rotation, matrix, Matrix3Helpers.CreateInverseTransform(position, rotation));
    }

    /// <summary>For visual endpoints only; simulation and network coordinates remain unchanged.</summary>
    public static MapCoordinates ToRenderMapCoordinates(this TransformSystem transforms, EntityCoordinates coordinates)
    {
        var parent = transforms.GetRenderMapCoordinates(coordinates.EntityId);
        return new MapCoordinates(Vector2.Transform(coordinates.Position,
            transforms.GetRenderWorldMatrix(coordinates.EntityId)), parent.MapId);
    }
}
