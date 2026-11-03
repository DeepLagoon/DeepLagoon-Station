using System.Numerics;
using Robust.Client.GameObjects;

namespace Content.Client._Goobstation.Weapons.LaserPointer;

/// <summary>
/// Keep the pointer's input ray and displayed ray in corresponding grid frames.
/// Simulation positions are used by raycasts; rendered positions by the camera.
/// </summary>
public static class LaserPointerRenderCoordinates
{
    private static Angle GridRotationDelta(TransformSystem transforms, TransformComponent source)
    {
        return source.GridUid is { } grid
            ? transforms.GetRenderWorldRotation(grid) - transforms.GetWorldRotation(grid)
            : Angle.Zero;
    }

    public static Vector2 ToSimulationDirection(TransformSystem transforms,
        Entity<TransformComponent> source, Vector2 renderedTarget)
    {
        var renderedDirection = renderedTarget - transforms.GetRenderWorldPosition((source.Owner, source.Comp));
        return (-GridRotationDelta(transforms, source.Comp)).RotateVec(renderedDirection);
    }

    public static (Vector2 Start, Vector2 End) GetLine(TransformSystem transforms,
        Entity<TransformComponent> source, Vector2 simulationDirection)
    {
        var start = transforms.GetRenderWorldPosition((source.Owner, source.Comp));
        return (start, start + GridRotationDelta(transforms, source.Comp).RotateVec(simulationDirection));
    }
}
