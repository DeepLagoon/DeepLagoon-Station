using System.Numerics;
using Content.Shared._Exodus.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Threading;

namespace Content.Server.Physics.Controllers;

public sealed partial class MoverController
{
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
        _collectMovement = _movementConfig.GetCVar(EXCVars.ParallelMoverUpdate) && count >= 128;
    }

    private void CompleteParallelMovement()
    {
        _collectMovement = false;
        if (_movementSteps.Count == 0)
            return;
        if (_movementVelocities.Length < _movementSteps.Count)
            Array.Resize(ref _movementVelocities, _movementSteps.Count);
        _movementParallel.ProcessNow(new MovementJob(_movementSteps, _movementVelocities), _movementSteps.Count);
        for (var i = 0; i < _movementSteps.Count; i++)
        {
            var step = _movementSteps[i];
            if (!TerminatingOrDeleted(step.Uid))
                FinishMobMovement(step, _movementVelocities[i]);
        }
        _movementSteps.Clear();
    }

    private readonly record struct MovementJob(List<MobMovementStep> Steps, Vector2[] Velocities) : IParallelRobustJob
    {
        public int BatchSize => 64;
        public void Execute(int index) => Velocities[index] = Steps[index].Calculate();
    }
}
