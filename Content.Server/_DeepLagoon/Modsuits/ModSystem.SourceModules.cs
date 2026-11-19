using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared._CE.ZLevels.Core.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Gravity;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Components;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Server._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModMagneticHarnessComponent : Component
{
    public EntityUid? Control;
    [ViewVariables] public string? LastReturnFailure;
}

[RegisterComponent]
public sealed partial class ModBatonResistanceComponent : Component
{
    public EntityUid? Control;
}

[RegisterComponent]
public sealed partial class ModJumpJetChargingComponent : Component;

[RegisterComponent]
public sealed partial class ModJumpJetFlightComponent : Component
{
    public TimeSpan Until;
    public MovementIgnoreGravityComponent? Applied;
}

public sealed partial class ModSystem
{
    [Dependency] private CESharedZLevelsSystem _modZLevels = default!;
    [Dependency] private SharedGravitySystem _modGravity = default!;
    private readonly List<(EntityUid Gun, EntityUid Wearer, EntityUid Suit, TimeSpan At)> _magneticReturns = new();

    private void InitializeSourceModules()
    {
        SubscribeLocalEvent<ModModuleComponent, ModJumpJetEvent>(OnJumpJetFinished);
        SubscribeLocalEvent<GunComponent, DroppedEvent>(OnMagneticDrop);
    }

    private void BindSourceEffect(IComponent effect, EntityUid? control)
    {
        if (effect is ModMagneticHarnessComponent harness) harness.Control = control;
        if (effect is ModBatonResistanceComponent resistance) resistance.Control = control;
    }

    public bool AbsorbBatonKnockdown(EntityUid wearer)
    {
        return TryComp<ModBatonResistanceComponent>(wearer, out var resistance) &&
            resistance.Control is {} suit && IsSealed(suit) && _power.TryUseCharge(suit, 50);
    }

    private void OnMagneticDrop(Entity<GunComponent> gun, ref DroppedEvent args)
    {
        if (TryComp<ModMagneticHarnessComponent>(args.User, out var harness) && harness.Control is {} suit)
        {
            harness.LastReturnFailure = "queued";
            _magneticReturns.Add((gun, args.User, suit, _timing.CurTime + TimeSpan.FromSeconds(0.5)));
        }
    }

    public bool StartJumpJet(Entity<ModControlComponent> suit, Entity<ModModuleComponent> module, EntityUid user)
    {
        if (module.Comp.Mode != ModModuleMode.JumpJet || module.Comp.Host != suit.Owner ||
            HasComp<ModJumpJetChargingComponent>(module) || module.Comp.NextUse > _timing.CurTime ||
            !IsSealed(suit) || !IsWorn(suit, user) || !CanUse(suit, user) || !_power.HasCharge(suit, module.Comp.UseCharge))
            return false;
        var action = new DoAfterArgs(EntityManager, user, 1, new ModJumpJetEvent(), module, target: user)
        {
            BreakOnMove = true, BreakOnDamage = true, NeedHand = false,
        };
        if (!_modDoAfter.TryStartDoAfter(action)) return false;
        EnsureComp<ModJumpJetChargingComponent>(module);
        return true;
    }

    private void OnJumpJetFinished(Entity<ModModuleComponent> module, ref ModJumpJetEvent args)
    {
        if (args.Handled) return;
        args.Handled = true;
        RemComp<ModJumpJetChargingComponent>(module);
        module.Comp.NextUse = _timing.CurTime + TimeSpan.FromSeconds(3);
        if (args.Cancelled || module.Comp.Host is not {} host || !TryComp<ModControlComponent>(host, out var control) ||
            !IsWorn(host, args.User) || !IsSealed(host) || !CanUse(host, args.User) || !_power.TryUseCharge(host, module.Comp.UseCharge))
            return;
        module.Comp.NextUse = _timing.CurTime + TimeSpan.FromSeconds(30);
        var flight = EnsureComp<ModJumpJetFlightComponent>(args.User);
        flight.Until = _timing.CurTime + TimeSpan.FromSeconds(5);
        if (!HasComp<MovementIgnoreGravityComponent>(args.User))
        {
            flight.Applied = new MovementIgnoreGravityComponent { Weightless = true };
            AddComp(args.User, flight.Applied);
        }
        if (!_modZLevels.HasTileAbove(args.User)) _modZLevels.TryMoveUp(args.User);
        foreach (var uid in control.Modules.ContainedEntities)
        {
            if (TryComp<ModModuleComponent>(uid, out var pack) && pack.Mode == ModModuleMode.Jetpack && !pack.Active)
                EnableJetpack(host, args.User, pack);
        }
    }

    private void UpdateSourceModules()
    {
        for (var i = _magneticReturns.Count - 1; i >= 0; i--)
        {
            var request = _magneticReturns[i];
            if (request.At > _timing.CurTime) continue;
            _magneticReturns.RemoveAt(i);
            if (TerminatingOrDeleted(request.Gun) || TerminatingOrDeleted(request.Wearer) || TerminatingOrDeleted(request.Suit) ||
                !TryComp<ModMagneticHarnessComponent>(request.Wearer, out var harness)) continue;
            harness.LastReturnFailure = harness.Control != request.Suit ? "different-controller" :
                !IsWorn(request.Suit, request.Wearer) ? "not-worn" : !IsSealed(request.Suit) ? "not-sealed" :
                _containers.IsEntityInContainer(request.Gun) ? "contained" :
                !_interaction.InRangeUnobstructed(request.Wearer, request.Gun, range: 1.5f) ? "not-adjacent" :
                !_power.HasCharge(request.Suit, 50) ? "no-power" : null;
            if (harness.LastReturnFailure != null) continue;
            // The harness extends suit storage to all guns, including weapons
            // whose native Clothing.Slots does not include suitStorage.
            if (_inventory.TryEquip(request.Wearer, request.Gun, "suitstorage", silent: true, force: true)) _power.TryUseCharge(request.Suit, 50);
            else harness.LastReturnFailure = "slot-insertion";
        }
        var flights = EntityQueryEnumerator<ModJumpJetFlightComponent>();
        while (flights.MoveNext(out var user, out var flight))
        {
            if (flight.Until > _timing.CurTime) continue;
            if (flight.Applied is {} applied && CompOrNull<MovementIgnoreGravityComponent>(user) == applied)
            {
                RemComp<MovementIgnoreGravityComponent>(user);
                _modGravity.RefreshWeightless(user);
            }
            RemCompDeferred<ModJumpJetFlightComponent>(user);
        }
    }
}
