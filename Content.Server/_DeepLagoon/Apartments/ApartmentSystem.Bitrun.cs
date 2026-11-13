using System.Linq;
using Content.Server.Popups;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared._Goobstation.EatToGrow;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Ghost;
using Content.Shared.Movement.Pulling.Components;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class BitrunApartmentPillComponent : Component
{
    [DataField]
    public bool ReturnToStop;
}

public sealed partial class ApartmentSystem
{
    private const string BitrunApartment = "DLApartmentBitrun";
    [Dependency] private ApartmentElevatorSystem _apartmentElevator = default!;
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SleepingSystem _sleeping = default!;
    [Dependency] private PopupSystem _bitrunPopups = default!;

    private void OnBitrunPillEaten(Entity<ApartmentResidentComponent> eater, ref AfterEatingEvent args)
    {
        if (!Enabled || !TryComp<BitrunApartmentPillComponent>(args.Food, out var pill) ||
            Transform(eater).GridUid is not { } grid || Transform(args.Food).GridUid != grid)
            return;

        var residents = new List<(NetUserId User, EntityUid Body)>();
        foreach (var (user, visit) in _visits)
        {
            if (visit.Apartment.Grid == grid && visit.Apartment.Template.ID == BitrunApartment &&
                !TerminatingOrDeleted(visit.Body) && !HasComp<GhostComponent>(visit.Body) &&
                Transform(visit.Body).GridUid == grid)
                residents.Add((user, visit.Body));
        }

        if (residents.All(resident => resident.Body != eater.Owner))
            return;

        if (residents.Any(resident => HasComp<ApartmentTripPendingComponent>(resident.Body)))
        {
            _bitrunPopups.PopupEntity(Loc.GetString("dl-apartment-trip-pending"), eater, eater);
            return;
        }

        if (pill.ReturnToStop)
        {
            if (_apartmentElevator.StartBitrunReturn(grid, residents) is { } error)
                _bitrunPopups.PopupEntity(Loc.GetString(error), eater, eater);
            return;
        }

        var beds = new List<EntityUid>();
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var uid))
        {
            if (TerminatingOrDeleted(uid) || !Transform(uid).Anchored || !HasComp<StrapComponent>(uid) ||
                MetaData(uid).EntityPrototype is not { } prototype ||
                !_prototypes.EnumerateParents(prototype, includeSelf: true).Any(parent => parent.ID == "Bed"))
                continue;
            beds.Add(uid);
        }

        if (beds.Count == 0)
        {
            _bitrunPopups.PopupEntity(Loc.GetString("dl-apartment-bitrun-no-bed"), eater, eater);
            return;
        }

        // Unbuckle the group first so occupied beds are freed before anyone is moved.
        foreach (var (_, body) in residents)
            PrepareBitrunTeleport(body);

        for (var index = 0; index < residents.Count; index++)
        {
            var body = residents[index].Body;
            var bed = beds[index % beds.Count];
            _transform.SetCoordinates(body, Transform(bed).Coordinates);
            if (index < beds.Count)
                _buckle.TryBuckle(body, null, bed, popup: false);
            _sleeping.TrySleeping(body);
        }
    }

    internal void MoveBitrunPassenger(EntityUid body, EntityCoordinates destination)
    {
        PrepareBitrunTeleport(body);
        _transform.SetCoordinates(body, destination);
    }

    private void PrepareBitrunTeleport(EntityUid body)
    {
        _buckle.Unbuckle(body, null);
        if (TryComp<PullerComponent>(body, out var puller) && puller.Pulling is { } pulled &&
            TryComp<PullableComponent>(pulled, out var pulledComp))
            _pulling.TryStopPull(pulled, pulledComp);
        if (TryComp<PullableComponent>(body, out var pullable))
            _pulling.TryStopPull(body, pullable);
        _containers.TryRemoveFromContainer(body, force: true);
        if (TryComp<PhysicsComponent>(body, out var physics))
            _physics.ResetDynamics(body, physics);
    }
}
