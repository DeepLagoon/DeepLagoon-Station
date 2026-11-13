using System.Numerics;
using Content.Shared.Ghost;
using Content.Shared.Movement.Components;
using Content.Shared.Rotation;
using Content.Shared.Standing;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedMoverController
{
    private const MoveButtons TiltButtons = MoveButtons.TiltLeft | MoveButtons.TiltRight;

    [Dependency] private SharedAppearanceSystem _movementAppearance = default!;
    [Dependency] private StandingStateSystem _standing = default!;

    internal static MoveButtons GetEffectiveMoveButtons(MoveButtons buttons)
    {
        if ((buttons & MoveButtons.TiltLeft) != 0)
            buttons &= ~MoveButtons.Left;
        if ((buttons & MoveButtons.TiltRight) != 0)
            buttons &= ~MoveButtons.Right;

        return buttons & ~(MoveButtons.Precision | TiltButtons);
    }

    private void UpdateCharacterTilt(Entity<InputMoverComponent> entity)
    {
        if (!HasComp<RotationVisualsComponent>(entity) || !TryComp<AppearanceComponent>(entity, out var appearance))
            return;

        var tilt = 0;
        if (entity.Comp.CanMove && !_mobState.IsIncapacitated(entity) && !_standing.IsDown(entity) &&
            !RelayQuery.HasComp(entity) && !PilotQuery.HasComp(entity))
        {
            tilt = (entity.Comp.HeldMoveButtons & TiltButtons) switch
            {
                MoveButtons.TiltLeft => 1,
                MoveButtons.TiltRight => -1,
                _ => 0,
            };
        }

        _movementAppearance.TryGetData<int>(entity, RotationVisuals.Tilt, out var previous, appearance);
        if (previous != tilt)
            _movementAppearance.SetData(entity, RotationVisuals.Tilt, tilt, appearance);
    }

    private static Vector2 LimitPrecisionMovement(Vector2 position, Vector2 velocity, Angle gridRotation,
        float tileSize, float frameTime)
    {
        if (frameTime <= 0f)
            return Vector2.Zero;

        // Work in grid coordinates so the boundary stays with the deck on a moving shuttle.
        var tileOrigin = new Vector2(MathF.Floor(position.X / tileSize), MathF.Floor(position.Y / tileSize)) * tileSize;
        var withinTile = position - tileOrigin;
        var margin = new Vector2(0.02f * tileSize);
        var min = Vector2.Min(margin, withinTile);
        var max = Vector2.Max(new Vector2(tileSize) - margin, withinTile);
        var localDisplacement = (-gridRotation).RotateVec(velocity) * frameTime;
        var target = Vector2.Clamp(withinTile + localDisplacement, min, max);
        return gridRotation.RotateVec((target - withinTile) / frameTime);
    }

    private sealed class CharacterMoveInputCmdHandler(SharedMoverController controller, MoveButtons button) : InputCmdHandler
    {
        public override bool HandleCmdMessage(IEntityManager entManager, ICommonSession? session, IFullInputCmdMessage message)
        {
            if (session?.AttachedEntity is { } uid && controller.MoverQuery.TryComp(uid, out var mover) &&
                (message.State == BoundKeyState.Up ||
                 (controller.MobMoverQuery.HasComp(uid) && !controller.HasComp<GhostComponent>(uid) &&
                  !controller.RelayQuery.HasComp(uid) && !controller.PilotQuery.HasComp(uid))))
            {
                controller.SetMoveInput((uid, mover), message.SubTick, message.State == BoundKeyState.Down, button);
            }

            return false;
        }
    }
}
