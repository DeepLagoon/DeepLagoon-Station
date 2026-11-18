using System.Numerics;
using Content.Shared._Exodus.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server.Physics.Controllers;

public sealed partial class MoverController
{
    private const int ParallelMovementThreshold = 128;
    [Dependency] private IParallelManager _movementParallel = default!;
    [Dependency] private IConfigurationManager _movementConfig = default!;
    private readonly List<MobMovementStep> _movementSteps = new();
    private Vector2[] _movementVelocities = Array.Empty<Vector2>();
    private bool _collectMovement;

    protected override bool QueueMobMovement(MobMovementStep step)
    {
        // Relay targets preserve their existing immediate ordering.
        if (!_collectMovement || step.RelaySource != null)
            return false;
        _movementSteps.Add(step);
        return true;
    }

    private void BeginParallelMovement(int count)
    {
        _movementSteps.Clear();
        _collectMovement = _movementConfig.GetCVar(EXCVars.ParallelMoverUpdate) && count >= ParallelMovementThreshold;
    }

    private void CompleteParallelMovement(bool measure, out long calculateTicks, out long applyTicks)
    {
        calculateTicks = applyTicks = 0;
        _collectMovement = false;
        if (_movementSteps.Count == 0)
            return;
        var started = measure ? Stopwatch.GetTimestamp() : 0;
        if (_movementVelocities.Length < _movementSteps.Count)
            Array.Resize(ref _movementVelocities, _movementSteps.Count);
        // Active markers also include unable-to-move mobs and relays. Only the
        // prepared steps determine whether dispatching a parallel job is useful.
        if (_movementSteps.Count >= ParallelMovementThreshold)
            _movementParallel.ProcessNow(new MovementJob(_movementSteps, _movementVelocities), _movementSteps.Count);
        else
            for (var i = 0; i < _movementSteps.Count; i++)
                _movementVelocities[i] = _movementSteps[i].Calculate();
        var calculated = measure ? Stopwatch.GetTimestamp() : 0;
        for (var i = 0; i < _movementSteps.Count; i++)
        {
            var step = _movementSteps[i];
            if (!step.Mover.Deleted && !step.Physics.Deleted && !step.Transform.Deleted &&
                !TerminatingOrDeleted(step.Uid) && !EntityManager.IsQueuedForDeletion(step.Uid))
                FinishMobMovement(step, _movementVelocities[i]);
        }
        _movementSteps.Clear();
        if (measure)
        {
            calculateTicks = calculated - started;
            applyTicks = Stopwatch.GetTimestamp() - calculated;
        }
    }

    private readonly record struct MovementJob(List<MobMovementStep> Steps, Vector2[] Velocities) : IParallelRobustJob
    {
        public int BatchSize => 64;
        public void Execute(int index) => Velocities[index] = Steps[index].Calculate();
    }
}
