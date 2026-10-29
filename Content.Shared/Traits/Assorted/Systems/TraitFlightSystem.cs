using Content.Shared.Actions;
using Content.Shared.ActionBlocker;
using Content.Shared.Bed.Sleep;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Traits.Assorted.Components;
using Robust.Shared.Network;

namespace Content.Shared.Traits.Assorted.Systems;

public sealed class TraitFlightSystem : EntitySystem
{
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private StaminaSystem _stamina = default!;
    [Dependency] private INetManager _net = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FlightComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<FlightComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<FlightComponent, ToggleFlightEvent>(OnToggle);
        SubscribeLocalEvent<FlightComponent, FlightDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<FlightComponent, IsWeightlessEvent>(OnWeightless);
        SubscribeLocalEvent<FlightComponent, CanWeightlessMoveEvent>(OnCanMove);
        SubscribeLocalEvent<FlightComponent, RefreshWeightlessModifiersEvent>(OnSpeed);
        SubscribeLocalEvent<FlightComponent, MobStateChangedEvent>(OnState);
        SubscribeLocalEvent<FlightComponent, SleepStateChangedEvent>(OnSleep);
        SubscribeLocalEvent<FlightComponent, KnockedDownEvent>(OnKnockedDown);
        SubscribeLocalEvent<FlightComponent, StunnedEvent>(OnStunned);
        SubscribeLocalEvent<FlightComponent, AfterAutoHandleStateEvent>(OnHandleState);
    }

    private void OnStartup(EntityUid uid, FlightComponent component, ComponentStartup args) =>
        _actions.AddAction(uid, ref component.ToggleActionEntity, component.ToggleAction);

    private void OnShutdown(EntityUid uid, FlightComponent component, ComponentShutdown args)
    {
        component.On = false;
        _actions.RemoveAction(uid, component.ToggleActionEntity);
        _gravity.RefreshWeightless(uid);
        _movement.RefreshWeightlessModifiers(uid);
    }

    private bool CanFly(EntityUid uid) => _mobState.IsAlive(uid) && _blocker.CanMove(uid)
        && !HasComp<KnockedDownComponent>(uid) && !HasComp<StunnedComponent>(uid);

    private void OnToggle(EntityUid uid, FlightComponent component, ToggleFlightEvent args)
    {
        if (args.Handled) return;
        if (component.On) SetActive(uid, component, false);
        else if (CanFly(uid))
            _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, uid, component.ActivationDelay,
                new FlightDoAfterEvent(), uid)
            { BreakOnDamage = true, BreakOnMove = false, BlockDuplicate = true, NeedHand = component.NeedsHands });
        args.Handled = true;
    }

    private void OnDoAfter(EntityUid uid, FlightComponent component, FlightDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !CanFly(uid)) return;
        SetActive(uid, component, true);
        args.Handled = true;
    }

    public void SetActive(EntityUid uid, FlightComponent component, bool active)
    {
        component.On = active;
        _actions.SetToggled(component.ToggleActionEntity, active);
        _gravity.RefreshWeightless(uid);
        _movement.RefreshWeightlessModifiers(uid);
        Dirty(uid, component);
    }

    private void OnHandleState(EntityUid uid, FlightComponent component, ref AfterAutoHandleStateEvent args)
    {
        _gravity.RefreshWeightless(uid);
        _movement.RefreshWeightlessModifiers(uid);
    }

    private void OnWeightless(EntityUid uid, FlightComponent component, ref IsWeightlessEvent args)
    {
        if (!component.On) return;
        args.IsWeightless = true;
        args.Handled = true;
    }

    private void OnCanMove(EntityUid uid, FlightComponent component, ref CanWeightlessMoveEvent args)
    {
        if (component.On) args.CanMove = true;
    }

    private void OnSpeed(EntityUid uid, FlightComponent component, ref RefreshWeightlessModifiersEvent args)
    {
        if (component.On) args.WeightlessModifier *= component.SpeedModifier;
    }

    private void OnState(EntityUid uid, FlightComponent component, MobStateChangedEvent args)
    {
        if (component.On && args.NewMobState != MobState.Alive) SetActive(uid, component, false);
    }
    private void OnSleep(EntityUid uid, FlightComponent component, ref SleepStateChangedEvent args)
    {
        if (component.On && args.FellAsleep) SetActive(uid, component, false);
    }
    private void OnKnockedDown(EntityUid uid, FlightComponent component, ref KnockedDownEvent args)
    {
        if (component.On) SetActive(uid, component, false);
    }
    private void OnStunned(EntityUid uid, FlightComponent component, ref StunnedEvent args)
    {
        if (component.On) SetActive(uid, component, false);
    }

    public override void Update(float frameTime)
    {
        if (_net.IsClient) return;
        var query = EntityQueryEnumerator<FlightComponent, StaminaComponent>();
        while (query.MoveNext(out var uid, out var flight, out var stamina))
            if (flight.On && (!CanFly(uid) || !_stamina.TryTakeStamina(uid, flight.StaminaDrainRate * frameTime, stamina)))
                SetActive(uid, flight, false);
    }
}
