using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelArousalSystem
{
    public void ApplyBlueMoonChemical(EntityUid uid, string chemical, float concentration)
    {
        if (!float.IsFinite(concentration) || concentration <= 0 || !EroticEffectsAllowed(uid)
            || !TryComp<MobStateComponent>(uid, out var mob) || mob.CurrentState == Content.Shared.Mobs.MobState.Dead) return;
        var preferences = Preferences(uid);
        if (chemical is "Crocin" or "Hexacrocin" && !preferences.Aphrodisiacs) return;
        var physiology = EnsureComp<BlueMoonPhysiologyComponent>(uid);
        if (physiology.NextChemicalEffect.GetValueOrDefault(chemical) > _timing.CurTime) return;
        physiology.NextChemicalEffect[chemical] = _timing.CurTime + TimeSpan.FromSeconds(2);
        if (chemical == "Hexacrocin" && concentration > 40) physiology.HexacrocinDependent = true;
        if (chemical == "Hexacrocin" && physiology.HexacrocinDependent) physiology.WithdrawalCycle = -15;
        var overdose = chemical == "Hexacrocin" && concentration > 40 || chemical == "Hexacamphor" && concentration > 20;
        if (overdose && physiology.Overdoses.Add(chemical)) RaiseLocalEvent(uid, new Content.Shared.Mood.MoodEffectEvent("DLPhysiologyOverdose"));
        else if (!overdose) physiology.Overdoses.Remove(chemical);
        var arousal = EnsureComp<InteractionPanelArousalComponent>(uid);
        RefreshMaximum(uid, arousal);
        switch (chemical)
        {
            case "Crocin":
            case "Hexacrocin":
                if (chemical == "Hexacrocin" && _random.Prob(.05f)) arousal.NeverAroused = false;
                if (arousal.NeverAroused) break;
                var limit = chemical == "Crocin" ? .3f : .95f;
                if (arousal.Current / arousal.Maximum < limit)
                    arousal.Current += Math.Min(12, Math.Max(0, arousal.Maximum - arousal.Current - 1));
                _bodyCustomization.SetGenitalArousal(uid, true);
                if (chemical == "Hexacrocin" && concentration > 40 && preferences.HormoneChanges)
                {
                    arousal.InHeat = true;
                    if (_random.Prob(.33f) && _random.Prob(.05f)) arousal.PermanentArousal = true;
                }
                break;
            case "Camphor":
                arousal.Current = Math.Max(0, arousal.Current - Math.Max(2, arousal.Current / 5));
                if (_random.Prob(.16f) && !arousal.PermanentArousal) _bodyCustomization.SetGenitalArousal(uid, false);
                break;
            case "Hexacamphor":
                arousal.Current = Math.Max(0, arousal.Current - Math.Max(10, arousal.Current / 4));
                arousal.PermanentArousal = arousal.InHeat = false;
                _bodyCustomization.SetGenitalArousal(uid, false);
                if (concentration > 20 && preferences.HormoneChanges && _random.Prob(.05f)) arousal.NeverAroused = true;
                break;
        }
        Dirty(uid, arousal);
    }
}
