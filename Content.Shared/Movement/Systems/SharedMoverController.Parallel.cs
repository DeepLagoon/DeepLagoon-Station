using System.Numerics;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics.Components;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedMoverController
{
    protected readonly record struct MobMovementStep(EntityUid Uid, InputMoverComponent Mover,
        PhysicsComponent Physics, TransformComponent Transform, ContentTileDefinition? Tile,
        EntityUid? RelaySource, bool Weightless, Vector2 WishDir, Vector2 Velocity,
        float MinimumFrictionSpeed, float FrameTime, float Friction, float Acceleration, bool CanAccelerate)
    {
        public Vector2 Calculate() => MovementVelocityMath.Calculate(Velocity, WishDir, MinimumFrictionSpeed,
            FrameTime, Friction, Acceleration, CanAccelerate);
    }
}

/// <summary>Pure locomotion arithmetic, shared by sequential and parallel server movement.</summary>
public static class MovementVelocityMath
{
    public static Vector2 Calculate(Vector2 velocity, Vector2 wishDir, float minimumFrictionSpeed,
        float frameTime, float friction, float acceleration, bool canAccelerate)
    {
        if (velocity.Length() >= minimumFrictionSpeed)
            velocity *= Math.Clamp(1f - frameTime * friction, 0f, 1f);
        if (canAccelerate)
            SharedMoverController.Accelerate(ref velocity, in wishDir, acceleration, frameTime);
        return velocity;
    }
}
