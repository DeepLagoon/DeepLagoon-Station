using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Robust.Client.GameObjects;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Client._DeepLagoon.Movement;

/// <summary>
/// Prevents map-space reconciliation from dragging a grounded player across a
/// moving deck. Physics prediction and local transform interpolation stay enabled.
/// </summary>
public sealed class MovingGridPredictionSystem : EntitySystem
{
    private const float MinGridSpeed = 0.0001f;
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private TransformSystem _transforms = default!;
    private EntityUid? _suppressedPlayer;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<InputMoverComponent, GetPredictionReconciliationTargetEvent>(OnReconciliationTarget);
    }

    private void OnReconciliationTarget(Entity<InputMoverComponent> ent,
        ref GetPredictionReconciliationTargetEvent args)
    {
        if (args.Target != ent.Owner)
            return;

        if (!TryComp(ent, out TransformComponent? xform)
            || xform.GridUid is not { } grid
            || xform.ParentUid != grid
            || !TryComp<PhysicsComponent>(ent, out var body)
            || body.BodyStatus != BodyStatus.OnGround
            || _gravity.IsWeightless(ent.Owner)
            || !TryComp<PhysicsComponent>(grid, out var gridBody)
            || (gridBody.LinearVelocity.LengthSquared() < MinGridSpeed * MinGridSpeed
                && MathF.Abs(gridBody.AngularVelocity) < MinGridSpeed))
        {
            if (_suppressedPlayer == ent.Owner)
                _suppressedPlayer = null;
            return;
        }

        // Clear a previous map-space correction once; keep interpolation on later ticks.
        if (_suppressedPlayer != ent.Owner)
        {
            _transforms.SnapRenderTransform(ent);
            _suppressedPlayer = ent.Owner;
        }

        // This query selects a visual reconciliation target. It does not disable
        // movement prediction, input replay or predictive interaction events.
        args.Target = EntityUid.Invalid;
    }
}
