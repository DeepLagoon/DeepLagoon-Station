using System.Numerics;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Ghost;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Movement.Components;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedMoverController
{
    [Dependency] private StaminaSystem _boostStamina = default!;

    /// <summary>
    /// Held, predicted input. Do not relay the boost to vehicles or change base speeds.
    /// </summary>
    public void SetSpeedBoost(Entity<InputMoverComponent> entity, ushort subTick, bool enabled)
    {
        SetMoveInput(entity, subTick, enabled, MoveButtons.SpeedBoost);
        if (!enabled && TryComp<StaminaComponent>(entity, out var stamina))
            _boostStamina.RecoverSpeedBoost((entity, stamina));
    }

    private Vector2 GetBoostedInputDirection(InputMoverComponent mover)
    {
        var direction = DirVecForButtons(mover.HeldMoveButtons);
        if ((mover.HeldMoveButtons & MoveButtons.SpeedBoost) == 0)
            return direction;

        // Multiply after normalizing diagonals, and before accumulating sub-tick input,
        // so pressing/releasing Alt partway through a tick only boosts that part.
        if (HasComp<AGhostSpeedComponent>(mover.Owner))
            return direction * 2f;

        if (CanCharacterSpeedBoost(mover))
            return direction * 1.35f;

        return direction;
    }

    private bool CanCharacterSpeedBoost(InputMoverComponent mover) =>
        (mover.HeldMoveButtons & MoveButtons.SpeedBoost) != 0 &&
        MobMoverQuery.HasComp(mover.Owner) && !HasComp<GhostComponent>(mover.Owner) &&
        TryComp<StaminaComponent>(mover.Owner, out var stamina) && _boostStamina.CanSpeedBoost(stamina);

    private void UpdateSpeedBoostStamina(Entity<InputMoverComponent> entity, float frameTime, Vector2 wishDir)
    {
        if (!Timing.InSimulation || HasComp<AGhostSpeedComponent>(entity) ||
            !TryComp<StaminaComponent>(entity, out var stamina))
            return;

        if ((entity.Comp.HeldMoveButtons & MoveButtons.SpeedBoost) == 0)
            _boostStamina.RecoverSpeedBoost((entity, stamina));

        if (wishDir == Vector2.Zero || !MobMoverQuery.HasComp(entity) || HasComp<GhostComponent>(entity))
            return;

        var currentTick = Timing.CurTick == entity.Comp.LastInputTick;
        var fraction = currentTick ? entity.Comp.CurTickSpeedBoostTime : 0f;
        if (CanCharacterSpeedBoost(entity.Comp) && DirVecForButtons(entity.Comp.HeldMoveButtons) != Vector2.Zero)
            fraction += currentTick ? (ushort.MaxValue - entity.Comp.LastInputSubTick) / (float) ushort.MaxValue : 1f;

        _boostStamina.ConsumeSpeedBoostStamina((entity, stamina), frameTime * fraction);
        entity.Comp.SpeedBoostActive = fraction > 0f && CanCharacterSpeedBoost(entity.Comp);
    }

    private sealed class SpeedBoostInputCmdHandler(SharedMoverController controller) : InputCmdHandler
    {
        public override bool HandleCmdMessage(IEntityManager entManager, ICommonSession? session, IFullInputCmdMessage message)
        {
            if (session?.AttachedEntity is { } uid && controller.MoverQuery.TryComp(uid, out var mover))
                controller.SetSpeedBoost((uid, mover), message.SubTick, message.State == BoundKeyState.Down);

            return false;
        }
    }
}

/// <summary>Actual character sprint started or stopped, including exhaustion and blocked movement.</summary>
[ByRefEvent]
public readonly record struct SpeedBoostChangedEvent(bool Active, bool Exhausted);
