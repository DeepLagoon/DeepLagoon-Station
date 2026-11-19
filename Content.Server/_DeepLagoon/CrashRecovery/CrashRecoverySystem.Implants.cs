using Content.Shared.Actions;
using Content.Shared.Implants.Components;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private BaseActionComponent? ImplantAction(EntityUid uid)
    {
        if (TryComp<InstantActionComponent>(uid, out var instant)) return instant;
        if (TryComp<EntityTargetActionComponent>(uid, out var entity)) return entity;
        if (TryComp<WorldTargetActionComponent>(uid, out var world)) return world;
        if (TryComp<EntityWorldTargetActionComponent>(uid, out var either)) return either;
        return null;
    }

    private void SaveImplantState(EntityUid uid)
    {
        if (!TryComp<SubdermalImplantComponent>(uid, out var implant)) return;
        var saved = EnsureComp<RecoveryImplantStateComponent>(uid);
        saved.Valid = false;
        if (implant.Action is not { } actionId || ImplantAction(actionId) is not { } action) return;
        saved.Valid = true;
        saved.Charges = action.Charges;
        saved.MaxCharges = action.MaxCharges;
        saved.Enabled = action.Enabled;
        saved.Toggled = action.Toggled;
        var now = _timing.CurTime - EntityManager.System<MetaDataSystem>().GetPauseTime(actionId);
        saved.CooldownRemaining = action.Cooldown is { } cooldown ? TimeSpan.FromTicks(Math.Max(0, (cooldown.End - now).Ticks)) : null;
    }

    public void RestoreImplantState(EntityUid uid)
    {
        if (!TryComp<RecoveryImplantStateComponent>(uid, out var saved) || !saved.Valid
            || !TryComp<SubdermalImplantComponent>(uid, out var implant)
            || implant.Action is not { } actionId || ImplantAction(actionId) is not { } action) return;
        action.Charges = saved.Charges;
        action.MaxCharges = saved.MaxCharges;
        action.Enabled = saved.Enabled;
        action.Toggled = saved.Toggled;
        action.Cooldown = saved.CooldownRemaining is { } remaining ? (_timing.CurTime, _timing.CurTime + remaining) : null;
        Dirty(actionId, action);
    }
}
