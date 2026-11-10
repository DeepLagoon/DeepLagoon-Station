using Content.Server.Power.Components;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Exodus.ShipShields;
using Robust.Shared.Map.Components;

namespace Content.Server._Crescent.ShipShields;

public sealed partial class ShipShieldsSystem
{
    private void RaiseShieldStateChanged(EntityUid? grid)
    {
        if (grid is not { } uid || TerminatingOrDeleted(uid) || !HasComp<MapGridComponent>(uid))
            return;

        var changed = new ShieldStatusDirtyEvent();
        RaiseLocalEvent(uid, ref changed);
    }

    public ShipShieldState? GetShieldState(EntityUid grid)
    {
        ShipShieldState? result = null;
        var query = EntityQueryEnumerator<ShipShieldEmitterComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var emitter, out var power, out var xform))
        {
            if (xform.GridUid != grid || TerminatingOrDeleted(uid))
                continue;
            var state = new ShipShieldState
            {
                BaseDraw = emitter.BaseDraw,
                Draw = CalculateLoadDamage(emitter),
                MaxDraw = emitter.MaxDraw,
                Recharging = emitter.Recharging,
                OverloadAccumulator = emitter.OverloadAccumulator,
                HealthFraction = emitter.DamageLimit > 0 ? Math.Clamp(1f - emitter.Damage / emitter.DamageLimit, 0f, 1f) : 0f,
                Active = emitter.Shield is { } shield && !TerminatingOrDeleted(shield),
                Powered = power.Powered,
            };
            // Prefer the active emitter, then a powered emitter, in installations with reserves.
            if (result == null || state.Active && !result.Value.Active
                || state.Active == result.Value.Active && state.Powered && !result.Value.Powered)
                result = state;
        }
        return result;
    }
}

[ByRefEvent]
internal readonly record struct ShieldStatusDirtyEvent;
