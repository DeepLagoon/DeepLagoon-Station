using Content.Shared.Mobs.Components;
using Content.Server.Actions;
using Content.Server.Humanoid;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Mobs;
using Content.Shared.Toggleable;
using Content.Shared.Wagging;
using Robust.Shared.Prototypes;

namespace Content.Server.Wagging;

/// <summary>
/// Adds an action to toggle wagging animation for tails markings that supporting this
/// </summary>
public sealed partial class WaggingSystem : EntitySystem
{
    [Dependency] private ActionsSystem _actions = default!;
    [Dependency] private HumanoidAppearanceSystem _humanoidAppearance = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    private float _refreshTime;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WaggingComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<WaggingComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<WaggingComponent, ToggleActionEvent>(OnToggle);
        SubscribeLocalEvent<WaggingComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
    }

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent args)
    {
        if (TryComp<HumanoidAppearanceComponent>(args.Mob, out var humanoid))
            RefreshAction(args.Mob, humanoid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // Markings can also be replaced by mirrors, admin tools, or appearance cloning.
        _refreshTime -= frameTime;
        if (_refreshTime > 0)
            return;
        _refreshTime = 0.5f;
        var query = EntityQueryEnumerator<HumanoidAppearanceComponent>();
        while (query.MoveNext(out var uid, out var humanoid))
            RefreshAction(uid, humanoid);
    }

    private void RefreshAction(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        TryComp<WaggingComponent>(uid, out var wagging);
        if (HasAnimatedTail(humanoid, wagging?.Suffix ?? WaggingMarkings.Suffix))
        {
            wagging ??= EnsureComp<WaggingComponent>(uid);
            if (!_actions.TryGetActionData(wagging.ActionEntity, out var action) || action.AttachedEntity != uid)
                _actions.AddAction(uid, ref wagging.ActionEntity, wagging.Action, uid);
            return;
        }

        if (wagging == null)
            return;
        if (wagging.Wagging)
        {
            wagging.Wagging = false;
            _actions.SetToggled(wagging.ActionEntity, false);
            Dirty(uid, wagging);
        }
        _actions.RemoveAction(wagging.ActionEntity);
    }

    private bool HasAnimatedTail(HumanoidAppearanceComponent humanoid, string suffix)
    {
        if (!humanoid.MarkingSet.Markings.TryGetValue(MarkingCategories.Tail, out var markings))
            return false;
        foreach (var marking in markings)
        {
            if (WaggingMarkings.TryGetPair(_prototype, marking.MarkingId, suffix, out _, out _))
                return true;
        }
        return false;
    }

    private void OnStartup(EntityUid uid, WaggingComponent component, ComponentStartup args)
    {
        if (TryComp<HumanoidAppearanceComponent>(uid, out var humanoid)
            && HasAnimatedTail(humanoid, component.Suffix))
            _actions.AddAction(uid, ref component.ActionEntity, component.Action, uid);
    }

    private void OnShutdown(EntityUid uid, WaggingComponent component, ComponentShutdown args)
        => _actions.RemoveAction(component.ActionEntity);

    private void OnToggle(EntityUid uid, WaggingComponent component, ref ToggleActionEvent args)
    {
        if (!args.Handled)
            args.Handled = TryToggleWagging(uid, wagging: component);
    }

    private void OnMobStateChanged(EntityUid uid, WaggingComponent component, MobStateChangedEvent args)
    {
        if (component.Wagging && args.NewMobState != MobState.Alive)
            TryToggleWagging(uid, wagging: component);
    }

    public bool TryToggleWagging(EntityUid uid, WaggingComponent? wagging = null, HumanoidAppearanceComponent? humanoid = null)
    {
        if (!Resolve(uid, ref wagging, ref humanoid) || !HasAnimatedTail(humanoid, wagging.Suffix))
            return false;
        if (!wagging.Wagging && TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState != MobState.Alive)
            return false;

        wagging.Wagging = !wagging.Wagging;
        _actions.SetToggled(wagging.ActionEntity, wagging.Wagging);
        Dirty(uid, wagging);

        var markings = humanoid.MarkingSet.Markings[MarkingCategories.Tail];
        for (var idx = 0; idx < markings.Count; idx++)
        {
            var current = markings[idx].MarkingId;
            if (!WaggingMarkings.TryGetPair(_prototype, current, wagging.Suffix, out var stationary, out var animated))
                continue;
            var target = wagging.Wagging ? animated : stationary;
            if (current != target)
                _humanoidAppearance.SetMarkingId(uid, MarkingCategories.Tail, idx, target, humanoid: humanoid);
        }
        return true;
    }
}
