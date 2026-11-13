using Content.Shared.Actions;
using Content.Shared._EE.CCVar;
using Content.Shared.Gravity;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedJetpackSystem : EntitySystem
{
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] protected SharedAppearanceSystem Appearance = default!;
    [Dependency] protected SharedContainerSystem Container = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private ActionContainerSystem _actionContainer = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private INetManager _net = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<JetpackComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<JetpackComponent, GetItemActionsEvent>(OnJetpackGetAction);
        SubscribeLocalEvent<JetpackComponent, DroppedEvent>(OnDropped);
        SubscribeLocalEvent<JetpackComponent, EntGotRemovedFromContainerMessage>(OnRemovedFromContainer);
        SubscribeLocalEvent<JetpackComponent, ComponentShutdown>(OnPackShutdown);
        SubscribeLocalEvent<JetpackComponent, ToggleJetpackEvent>(OnToggle);
        SubscribeLocalEvent<JetpackSelectionComponent, WeightlessnessChangedEvent>(OnWeightlessness);
        SubscribeLocalEvent<JetpackSelectionComponent, EntParentChangedMessage>(OnParentChanged,
            after: [typeof(SharedGravitySystem)]);
        SubscribeLocalEvent<JetpackSelectionComponent, EntityTerminatingEvent>(OnUserTerminating);
        SubscribeLocalEvent<JetpackSelectionComponent, ComponentShutdown>(OnSelectionShutdown);

        SubscribeLocalEvent<JetpackUserComponent, RefreshWeightlessModifiersEvent>(OnModifiers);
        SubscribeLocalEvent<JetpackUserComponent, CanWeightlessMoveEvent>(OnCanMove);
        SubscribeLocalEvent<JetpackUserComponent, ComponentShutdown>(OnFlightShutdown);
    }

    private void OnMapInit(EntityUid uid, JetpackComponent component, MapInitEvent args)
    {
        _actionContainer.EnsureAction(uid, ref component.ToggleActionEntity, component.ToggleAction);
        Dirty(uid, component);
    }

    private void OnJetpackGetAction(EntityUid uid, JetpackComponent component, GetItemActionsEvent args)
        => args.AddAction(ref component.ToggleActionEntity, component.ToggleAction);

    private void OnToggle(EntityUid uid, JetpackComponent pack, ToggleJetpackEvent args)
    {
        if (args.Handled || _net.IsClient)
            return;

        SetEnabled(uid, pack, pack.JetpackUser != args.Performer, args.Performer);
        args.Handled = true;
    }

    private void OnDropped(EntityUid uid, JetpackComponent component, DroppedEvent args)
        => SetEnabled(uid, component, false);

    private void OnRemovedFromContainer(EntityUid uid, JetpackComponent pack, EntGotRemovedFromContainerMessage args)
        => SetEnabled(uid, pack, false);

    private void OnPackShutdown(Entity<JetpackComponent> pack, ref ComponentShutdown args)
        => SetEnabled(pack, pack.Comp, false);

    private void OnWeightlessness(Entity<JetpackSelectionComponent> user, ref WeightlessnessChangedEvent args)
        => RefreshFlight(user, user.Comp.Pack);

    private void OnParentChanged(Entity<JetpackSelectionComponent> user, ref EntParentChangedMessage args)
        => RefreshFlight(user, user.Comp.Pack);

    private void OnUserTerminating(Entity<JetpackSelectionComponent> user, ref EntityTerminatingEvent args)
        => DisableSelectedPack(user);

    private void OnSelectionShutdown(Entity<JetpackSelectionComponent> user, ref ComponentShutdown args)
        => DisableSelectedPack(user);

    private void DisableSelectedPack(Entity<JetpackSelectionComponent> user)
    {
        if (TryComp<JetpackComponent>(user.Comp.Pack, out var pack) && pack.JetpackUser == user.Owner)
            SetEnabled(user.Comp.Pack, pack, false);
    }

    private void OnFlightShutdown(Entity<JetpackUserComponent> user, ref ComponentShutdown args)
    {
        if (_net.IsServer && HasComp<ActiveJetpackComponent>(user.Comp.Jetpack) &&
            TryComp<JetpackComponent>(user.Comp.Jetpack, out var pack) && pack.JetpackUser == user.Owner)
        {
            SetEnabled(user.Comp.Jetpack, pack, false);
        }
    }

    private void OnModifiers(Entity<JetpackUserComponent> user, ref RefreshWeightlessModifiersEvent args)
    {
        if (user.Comp.LifeStage >= ComponentLifeStage.Stopping)
            return;

        args.WeightlessAcceleration = user.Comp.WeightlessAcceleration;
        args.WeightlessModifier = user.Comp.WeightlessModifier;
        args.WeightlessFriction = user.Comp.WeightlessFriction;
        args.WeightlessFrictionNoInput = user.Comp.WeightlessFrictionNoInput;
    }

    private void OnCanMove(EntityUid uid, JetpackUserComponent component, ref CanWeightlessMoveEvent args)
    {
        if (component.LifeStage < ComponentLifeStage.Stopping)
            args.CanMove = true;
    }

    public void SetEnabled(EntityUid uid, JetpackComponent component, bool enabled, EntityUid? user = null)
    {
        if (_net.IsClient)
            return;

        if (!enabled)
        {
            if (component.JetpackUser is not { } owner)
                return;

            component.JetpackUser = null;
            if (TryComp<JetpackSelectionComponent>(owner, out var selection) &&
                selection.Pack == uid && selection.LifeStage < ComponentLifeStage.Stopping)
                RemComp<JetpackSelectionComponent>(owner);

            SetThrust(owner, (uid, component), false);
            _actions.SetToggled(component.ToggleActionEntity, false);
            if (!TerminatingOrDeleted(uid))
                Dirty(uid, component);
            return;
        }

        if (!Container.TryGetContainingContainer((uid, null, null), out var container))
            return;

        user ??= container.Owner;
        if (container.Owner != user.Value || TerminatingOrDeleted(user.Value) ||
            TerminatingOrDeleted(uid) || !CanEnable(uid, user.Value, component))
        {
            return;
        }

        if (TryComp<JetpackSelectionComponent>(user, out var previous) && previous.Pack != uid &&
            TryComp<JetpackComponent>(previous.Pack, out var oldPack))
        {
            SetEnabled(previous.Pack, oldPack, false);
        }

        if (component.JetpackUser is { } other && other != user.Value)
            SetEnabled(uid, component, false);

        EnsureComp<JetpackSelectionComponent>(user.Value).Pack = uid;
        component.JetpackUser = user.Value;
        Dirty(uid, component);
        _actions.SetToggled(component.ToggleActionEntity, true);
        RefreshFlight(user.Value, uid);
    }

    private void RefreshFlight(EntityUid user, EntityUid uid)
    {
        if (_net.IsClient || !TryComp<JetpackComponent>(uid, out var pack) ||
            pack.JetpackUser != user || TerminatingOrDeleted(user))
        {
            return;
        }

        var grid = Transform(user).GridUid;
        var mayFly = _gravity.IsWeightless(user) && UserNotParented(user, pack) &&
            (grid == null || _config.GetCVar(EECCVars.JetpackEnableAnywhere) ||
             _config.GetCVar(EECCVars.JetpackEnableInNoGravity));

        if (mayFly && !CanEnable(uid, user, pack))
        {
            SetEnabled(uid, pack, false);
            return;
        }

        SetThrust(user, (uid, pack), mayFly);
    }

    private void SetThrust(EntityUid user, Entity<JetpackComponent> pack, bool active)
    {
        if (active)
        {
            if (HasComp<ActiveJetpackComponent>(pack))
                return;

            var flight = EnsureComp<JetpackUserComponent>(user);
            flight.Jetpack = pack;
            flight.WeightlessAcceleration = pack.Comp.Acceleration;
            flight.WeightlessModifier = pack.Comp.WeightlessModifier;
            flight.WeightlessFriction = pack.Comp.Friction;
            flight.WeightlessFrictionNoInput = pack.Comp.Friction;
            Dirty(user, flight);
            EnsureComp<ActiveJetpackComponent>(pack);
        }
        else
        {
            RemComp<ActiveJetpackComponent>(pack);
            if (!TryComp<JetpackUserComponent>(user, out var flight) || flight.Jetpack != pack.Owner)
                return;

            if (flight.LifeStage < ComponentLifeStage.Stopping)
                RemComp<JetpackUserComponent>(user);
        }

        if (TryComp<PhysicsComponent>(user, out var physics))
            _physics.SetBodyStatus(user, physics, active ? BodyStatus.InAir : BodyStatus.OnGround);

        _movement.RefreshWeightlessModifiers(user);
        if (!TerminatingOrDeleted(pack))
            Appearance.SetData(pack, JetpackVisuals.Enabled, active);
    }

    public bool IsUserFlying(EntityUid uid)
        => TryComp<JetpackUserComponent>(uid, out var flight) && flight.LifeStage < ComponentLifeStage.Stopping;

    protected virtual bool CanEnable(EntityUid uid, EntityUid user, JetpackComponent component) => true;

    protected virtual bool UserNotParented(EntityUid? user, JetpackComponent component)
    {
        return !TryComp(user, out TransformComponent? xform)
            || xform.ParentUid == xform.GridUid
            || xform.ParentUid == xform.MapUid;
    }
}

[Serializable, NetSerializable]
public enum JetpackVisuals : byte
{
    Enabled,
}
