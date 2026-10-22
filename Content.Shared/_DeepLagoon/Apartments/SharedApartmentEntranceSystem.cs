using Content.Shared._Mono.NoDeconstruct;
using Content.Shared.Damage;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Interaction;
using Content.Shared.Prying.Components;
using Robust.Shared.Network;
using static Content.Shared.Damage.DamageableSystem;

namespace Content.Shared._DeepLagoon.Apartments;

/// <summary>Protects the entrance on both sides, including predicted door interactions.</summary>
public sealed partial class SharedApartmentEntranceSystem : EntitySystem
{
    [Dependency] private INetManager _net = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentEntranceComponent, ActivateInWorldEvent>(OnActivate,
            before: new[] { typeof(SharedDoorSystem), typeof(SharedAirlockSystem) });
        SubscribeLocalEvent<ApartmentEntranceComponent, BeforeDoorOpenedEvent>((uid, comp, args) => args.Cancel());
        SubscribeLocalEvent<ApartmentEntranceComponent, DoorStateChangedEvent>(OnDoorState);
        SubscribeLocalEvent<ApartmentEntranceComponent, BeforePryEvent>(OnPry);
        SubscribeLocalEvent<ApartmentEntranceComponent, BeforeDamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<ApartmentEntranceComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ApartmentEntranceComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnActivate(EntityUid uid, ApartmentEntranceComponent comp, ActivateInWorldEvent args)
    {
        if (!args.Complex) return;
        // Even if another handler denied opening, the click still opens the travel menu.
        args.Handled = true;
        RaiseLocalEvent(uid, new ApartmentEntranceActivatedEvent(args.User));
    }

    private void OnPry(EntityUid uid, ApartmentEntranceComponent comp, ref BeforePryEvent args) => args.Cancelled = true;
    private void OnDamage(EntityUid uid, ApartmentEntranceComponent comp, ref BeforeDamageChangedEvent args) => args.Cancelled = true;

    private void OnDoorState(EntityUid uid, ApartmentEntranceComponent comp, DoorStateChangedEvent args)
    {
        if (args.State != DoorState.Closed) _doors.SetState(uid, DoorState.Closed);
    }

    private void OnStartup(EntityUid uid, ApartmentEntranceComponent comp, ComponentStartup args)
    {
        if (!_net.IsServer) return;
        comp.AddedNoDeconstruct |= !HasComp<NoDeconstructComponent>(uid);
        EnsureComp<NoDeconstructComponent>(uid);
    }

    private void OnShutdown(EntityUid uid, ApartmentEntranceComponent comp, ComponentShutdown args)
    {
        if (_net.IsServer && comp.AddedNoDeconstruct && !TerminatingOrDeleted(uid)) RemComp<NoDeconstructComponent>(uid);
    }
}
