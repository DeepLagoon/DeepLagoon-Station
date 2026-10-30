using System.Collections.Generic;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    private readonly HashSet<EntityUid> _flightDockedGrids = new();
    private readonly HashSet<EntityUid> _flightProcessedGrids = new();

    private void InitializeFlightLimits()
    {
        SubscribeLocalEvent<PhysicsUpdateBeforeSolveEvent>(OnFlightLimitsBeforeSolve);
        SubscribeLocalEvent<PhysicsUpdateAfterSolveEvent>(OnFlightLimitsAfterSolve);
    }

    private void OnFlightLimitsBeforeSolve(ref PhysicsUpdateBeforeSolveEvent args) => LimitFlightVelocity();
    private void OnFlightLimitsAfterSolve(ref PhysicsUpdateAfterSolveEvent args) => LimitFlightVelocity();

    private void LimitFlightVelocity()
    {
        _flightProcessedGrids.Clear();
        var query = EntityQueryEnumerator<ShuttleComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var shuttle, out var body))
        {
            if (!shuttle.Enabled || !body.Awake || !_flightProcessedGrids.Add(uid) ||
                TryComp<FTLComponent>(uid, out var ftl) && ftl.State is FTLState.Travelling or FTLState.Arriving)
                continue;

            // A docked unpowered grid must not clamp the ship towing it to the fallback T1 limit.
            _flightDockedGrids.Clear();
            GetAllDockedShuttlesIgnoringFTLLock(uid, _flightDockedGrids);
            _flightDockedGrids.Add(uid);
            var maximum = ShuttleFlightLimits.GridMaxSpeed(shuttle);
            foreach (var docked in _flightDockedGrids)
            {
                if (TryComp<ShuttleComponent>(docked, out var other) && other.Enabled)
                    maximum = MathF.Max(maximum, ShuttleFlightLimits.GridMaxSpeed(other));
            }

            foreach (var docked in _flightDockedGrids)
            {
                _flightProcessedGrids.Add(docked);
                if (TryComp<PhysicsComponent>(docked, out var otherBody) &&
                    otherBody.LinearVelocity.LengthSquared() > maximum * maximum)
                    _physics.SetLinearVelocity(docked, Vector2.Normalize(otherBody.LinearVelocity) * maximum, body: otherBody);
            }
        }
    }
}
