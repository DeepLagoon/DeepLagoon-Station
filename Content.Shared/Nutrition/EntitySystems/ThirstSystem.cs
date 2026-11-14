using Content.Shared.Alert;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Rejuvenate;
using Content.Shared.StatusIcon;
using JetBrains.Annotations;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Diagnostics.CodeAnalysis;

namespace Content.Shared.Nutrition.EntitySystems;

[UsedImplicitly]
public sealed partial class ThirstSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private AlertsSystem _alerts = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private SharedJetpackSystem _jetpack = default!;

    private static readonly ProtoId<SatiationIconPrototype> ThirstIconOverhydratedId = "ThirstIconOverhydrated";

    private static readonly ProtoId<SatiationIconPrototype> ThirstIconThirstyId = "ThirstIconThirsty";

    private static readonly ProtoId<SatiationIconPrototype> ThirstIconParchedId = "ThirstIconParched";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ThirstComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovespeed);
        SubscribeLocalEvent<ThirstComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ThirstComponent, RejuvenateEvent>(OnRejuvenate);
    }

    private void OnMapInit(EntityUid uid, ThirstComponent component, MapInitEvent args)
    {
        // Do not change behavior unless starting value is explicitly defined
        if (component.CurrentThirst < 0)
        {
            component.CurrentThirst = _random.Next(
                (int) component.ThirstThresholds[ThirstThreshold.Thirsty] + 10,
                (int) component.ThirstThresholds[ThirstThreshold.Okay] - 1);
        }
        component.NextUpdateTime = _timing.CurTime;
        component.CurrentThirstThreshold = GetThirstThreshold(component, component.CurrentThirst);
        component.LastThirstThreshold = ThirstThreshold.Okay; // TODO: Potentially change this -> Used Okay because no effects.
        // TODO: Check all thresholds make sense and throw if they don't.
        UpdateEffects(uid, component);

        TryComp(uid, out MovementSpeedModifierComponent? moveMod);
            _movement.RefreshMovementSpeedModifiers(uid, moveMod);
    }

    private void OnRefreshMovespeed(EntityUid uid, ThirstComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        // TODO: This should really be taken care of somewhere else
        if (_jetpack.IsUserFlying(uid))
            return;

        if (IsSlowedByThirst(component.CurrentThirstThreshold))
            args.ModifySpeed(0.75f);
    }

    private void OnRejuvenate(EntityUid uid, ThirstComponent component, RejuvenateEvent args)
    {
        SetThirst(uid, component, component.ThirstThresholds[ThirstThreshold.Okay]);
    }

    private ThirstThreshold GetThirstThreshold(ThirstComponent component, float amount)
    {
        ThirstThreshold result = ThirstThreshold.Dead;
        var value = component.ThirstThresholds[ThirstThreshold.OverHydrated];
        foreach (var threshold in component.ThirstThresholds)
        {
            if (threshold.Value <= value && threshold.Value >= amount)
            {
                result = threshold.Key;
                value = threshold.Value;
            }
        }

        return result;
    }

    public void ModifyThirst(EntityUid uid, ThirstComponent component, float amount)
    {
        SetThirst(uid, component, component.CurrentThirst + amount);
    }

    public void SetThirst(EntityUid uid, ThirstComponent component, float amount)
    {
        var thirst = Math.Clamp(amount,
            component.ThirstThresholds[ThirstThreshold.Dead],
            component.ThirstThresholds[ThirstThreshold.OverHydrated]
        );
        if (component.CurrentThirst == thirst)
            return;

        component.CurrentThirst = thirst;
        DirtyField(uid, component, nameof(ThirstComponent.CurrentThirst));
    }

    private static bool IsSlowedByThirst(ThirstThreshold threshold) => threshold <= ThirstThreshold.Parched;

    public bool TryGetStatusIconPrototype(ThirstComponent component, [NotNullWhen(true)] out SatiationIconPrototype? prototype)
    {
        switch (component.CurrentThirstThreshold)
        {
            case ThirstThreshold.OverHydrated:
                _prototype.TryIndex(ThirstIconOverhydratedId, out prototype);
                break;

            case ThirstThreshold.Thirsty:
                _prototype.TryIndex(ThirstIconThirstyId, out prototype);
                break;

            case ThirstThreshold.Parched:
                _prototype.TryIndex(ThirstIconParchedId, out prototype);
                break;

            default:
                prototype = null;
                break;
        }

        return prototype != null;
    }

    private void UpdateEffects(EntityUid uid, ThirstComponent component)
    {
        var threshold = component.CurrentThirstThreshold;
        RaiseLocalEvent(uid, new Content.Shared.Mood.MoodEffectEvent("Thirst" + threshold));
        if (IsSlowedByThirst(component.LastThirstThreshold) != IsSlowedByThirst(threshold))
        {
            _movement.RefreshMovementSpeedModifiers(uid);
        }

        // Update UI
        if (ThirstComponent.ThirstThresholdAlertTypes.TryGetValue(threshold, out var alertId))
        {
            _alerts.ShowAlert(uid, alertId);
        }
        else
        {
            _alerts.ClearAlertCategory(uid, component.ThirstyCategory);
        }

        component.LastThirstThreshold = threshold;
        component.ActualDecayRate = threshold switch
        {
            ThirstThreshold.OverHydrated => component.BaseDecayRate * 1.2f,
            ThirstThreshold.Okay => component.BaseDecayRate,
            ThirstThreshold.Thirsty => component.BaseDecayRate * 0.8f,
            ThirstThreshold.Parched => component.BaseDecayRate * 0.6f,
            ThirstThreshold.Dead => component.ActualDecayRate,
            _ => throw new ArgumentOutOfRangeException(nameof(threshold), threshold, null),
        };
        DirtyField(uid, component, nameof(ThirstComponent.CurrentThirstThreshold));
        DirtyField(uid, component, nameof(ThirstComponent.LastThirstThreshold));
        DirtyField(uid, component, nameof(ThirstComponent.ActualDecayRate));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // The client uses the replicated thirst threshold when recalculating its predicted speed.
        if (_net.IsClient)
            return;

        var query = EntityQueryEnumerator<ThirstComponent>();
        while (query.MoveNext(out var uid, out var thirst))
        {
            if (_timing.CurTime < thirst.NextUpdateTime)
                continue;

            thirst.NextUpdateTime += thirst.UpdateRate;

            ModifyThirst(uid, thirst, -thirst.ActualDecayRate);
            var calculatedThirstThreshold = GetThirstThreshold(thirst, thirst.CurrentThirst);

            if (calculatedThirstThreshold == thirst.CurrentThirstThreshold)
                continue;

            thirst.CurrentThirstThreshold = calculatedThirstThreshold;
            UpdateEffects(uid, thirst);
        }
    }
}
