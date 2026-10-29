using Content.Shared.Weapons.Melee.Components;
using Content.Shared.Contests;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Traits.Assorted.Components;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Prototypes;

namespace Content.Shared.Traits.Assorted.Systems;

/// <summary>EE trait effects applied through DeepLagoon's combat and damage events.</summary>
public sealed class TraitStatModifierSystem : EntitySystem
{
    [Dependency] private ContestsSystem _contests = default!;
    [Dependency] private MobThresholdSystem _thresholds = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<CritModifierComponent, ComponentStartup>(OnCrit);
        SubscribeLocalEvent<DeadModifierComponent, ComponentStartup>(OnDead);
        SubscribeLocalEvent<AdrenalineComponent, GetMeleeDamageEvent>(OnAdrenaline);
        SubscribeLocalEvent<PainToleranceComponent, GetMeleeDamageEvent>(OnPain);
        SubscribeLocalEvent<TraitMartialArtistComponent, GetMeleeDamageEvent>(OnMartialArtist);
        SubscribeLocalEvent<OniDamageModifierComponent, GetMeleeDamageEvent>(OnOni);
        SubscribeLocalEvent<TraitArmorComponent, DamageModifyEvent>(OnArmor);
    }

    private void OnCrit(EntityUid uid, CritModifierComponent component, ComponentStartup args)
    {
        if (!TryComp<MobThresholdsComponent>(uid, out var thresholds)) return;
        var current = _thresholds.GetThresholdForState(uid, MobState.Critical, thresholds);
        if (current != 0) _thresholds.SetMobStateThreshold(uid, current + component.CritThresholdModifier, MobState.Critical);
    }

    private void OnDead(EntityUid uid, DeadModifierComponent component, ComponentStartup args)
    {
        if (!TryComp<MobThresholdsComponent>(uid, out var thresholds)) return;
        var current = _thresholds.GetThresholdForState(uid, MobState.Dead, thresholds);
        if (current != 0) _thresholds.SetMobStateThreshold(uid, current + component.DeadThresholdModifier, MobState.Dead);
    }

    private void OnAdrenaline(EntityUid uid, AdrenalineComponent component, ref GetMeleeDamageEvent args)
    {
        var modifier = _contests.HealthContest(uid, component.BypassClamp, component.RangeModifier);
        args.Damage *= component.Inverse ? 1f / modifier : modifier;
    }

    private void OnPain(EntityUid uid, PainToleranceComponent component, ref GetMeleeDamageEvent args)
    {
        var modifier = _contests.StaminaContest(uid, component.BypassClamp, component.RangeModifier);
        args.Damage *= component.Inverse ? 1f / modifier : modifier;
    }

    private void OnMartialArtist(EntityUid uid, TraitMartialArtistComponent component, ref GetMeleeDamageEvent args)
    {
        if (args.Weapon == uid) args.Modifiers.Add(component.Modifiers);
    }

    private void OnOni(EntityUid uid, OniDamageModifierComponent component, ref GetMeleeDamageEvent args)
    {
        if (args.User != uid || args.Weapon == uid) return;
        TryComp<BonusMeleeDamageComponent>(uid, out var bonus);
        var modifiers = new DamageModifierSet();
        foreach (var (type, value) in component.Modifiers.Coefficients)
        {
            var baseline = bonus?.DamageModifierSet?.Coefficients.GetValueOrDefault(type, 1f) ?? 1f;
            modifiers.Coefficients[type] = baseline > 0f ? value / baseline : 1f;
        }
        args.Modifiers.Add(modifiers);
    }

    private void OnArmor(EntityUid uid, TraitArmorComponent component, DamageModifyEvent args)
    {
        foreach (var id in component.ModifierSets)
            args.Damage = DamageSpecifier.ApplyModifierSet(args.Damage, _prototypes.Index(id));
    }
}
