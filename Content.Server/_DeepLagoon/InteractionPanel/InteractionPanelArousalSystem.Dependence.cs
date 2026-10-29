using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mood;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem
{
    private void UpdateDrugDependence(EntityUid uid, BlueMoonPhysiologyComponent state)
    {
        if (!state.HexacrocinDependent) return;
        if (!Preferences(uid).Aphrodisiacs || !EroticEffectsAllowed(uid))
        {
            ClearWithdrawal(uid, state);
            return;
        }
        if (!TryComp<MobStateComponent>(uid, out var mob) || mob.CurrentState == MobState.Dead || state.NextWithdrawal > _timing.CurTime) return;
        state.NextWithdrawal = _timing.CurTime + TimeSpan.FromSeconds(12);
        if (_solutions.TryGetSolution(uid, "chemicals", out _, out var contents) && contents.GetTotalPrototypeQuantity("DLHexacrocin") > 0)
            state.WithdrawalCycle = -15;
        if (++state.WithdrawalCycle > 40)
        {
            ClearWithdrawal(uid, state);
            return;
        }
        var stage = state.WithdrawalCycle > 0 ? (state.WithdrawalCycle - 1) / 10 + 1 : 0;
        RemoveWithdrawalMood(uid);
        if (stage == 0) return;
        RaiseLocalEvent(uid, new MoodEffectEvent("DLHexacrocinWithdrawal" + stage));
        if (stage >= 2 && _random.Prob(.3f))
            EntityManager.System<DamageableSystem>().TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = new() { ["Cellular"] = FixedPoint2.New(stage) } }, ignoreResistances: true);
    }

    private void RemoveWithdrawalMood(EntityUid uid)
    {
        for (var stage = 1; stage <= 4; stage++) RaiseLocalEvent(uid, new MoodRemoveEffectEvent("DLHexacrocinWithdrawal" + stage));
    }

    private void ClearWithdrawal(EntityUid uid, BlueMoonPhysiologyComponent state)
    {
        state.HexacrocinDependent = false;
        state.WithdrawalCycle = 0;
        RemoveWithdrawalMood(uid);
    }
}
