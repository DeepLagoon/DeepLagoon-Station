using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared._White.Standing;
using Content.Shared.ActionBlocker;
using Content.Shared.Chemistry.Components;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Medical;

public sealed partial class CriticalCrawlingSystem : EntitySystem
{
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private BloodstreamSystem _bloodstream = default!;
    [Dependency] private MobThresholdSystem _thresholds = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MobThresholdsComponent, MobThresholdChecked>(OnThresholdChecked);
        SubscribeLocalEvent<MobThresholdsComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<MobThresholdsComponent, PlayerDetachedEvent>(OnPlayerDetached);
    }

    private void OnThresholdChecked(Entity<MobThresholdsComponent> entity, ref MobThresholdChecked args)
        => Refresh(entity, args.MobState, args.Damageable);

    private void OnPlayerAttached(EntityUid uid, MobThresholdsComponent thresholds, PlayerAttachedEvent args)
        => RefreshAfterAttachmentChange((uid, thresholds));

    private void OnPlayerDetached(EntityUid uid, MobThresholdsComponent thresholds, PlayerDetachedEvent args)
        => RefreshAfterAttachmentChange((uid, thresholds));

    private void RefreshAfterAttachmentChange(Entity<MobThresholdsComponent> entity)
    {
        if (TryComp<MobStateComponent>(entity, out var state) && TryComp<DamageableComponent>(entity, out var damageable))
            Refresh(entity, state, damageable);
    }

    private void Refresh(Entity<MobThresholdsComponent> entity, MobStateComponent state, DamageableComponent damageable)
    {
        if (TerminatingOrDeleted(entity))
            return;

        if (state.CurrentState != MobState.Critical)
        {
            if (RemComp<CriticalCrawlingComponent>(entity))
                _blocker.UpdateCanMove(entity);

            return;
        }

        var damage = damageable.TotalDamage;
        var eligible = HasComp<ActorComponent>(entity) &&
            _thresholds.TryGetThresholdForState(entity, MobState.Critical, out var critical, entity.Comp) &&
            _thresholds.TryGetDeadThreshold(entity, out var dead, entity.Comp) && dead > critical &&
            damage - critical.Value <= dead.Value - damage;

        if (!TryComp<CriticalCrawlingComponent>(entity, out var crawling))
        {
            if (!eligible)
                return;

            crawling = AddComp<CriticalCrawlingComponent>(entity);
        }

        var canCrawl = eligible && HasComp<LayingDownComponent>(entity);
        if (crawling.CanCrawl != canCrawl)
        {
            crawling.CanCrawl = canCrawl;
            Dirty(entity, crawling);
            _blocker.UpdateCanMove(entity);
        }

        if (!eligible || crawling.AdrenalineInjected || !TryComp<BloodstreamComponent>(entity, out var bloodstream))
            return;

        // Threshold and solution events can both run during the same damage change.
        crawling.AdrenalineInjected = true;
        if (!_bloodstream.TryAddToChemicals(entity, new Solution("Epinephrine", 5), bloodstream))
            crawling.AdrenalineInjected = false;
    }
}
