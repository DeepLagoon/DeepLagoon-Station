using Content.Shared.Chemistry.EntitySystems;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Fluids.Components;
using JetBrains.Annotations;

namespace Content.Server.Destructible.Thresholds.Behaviors
{
    [UsedImplicitly]
    [DataDefinition]
    public sealed partial class SpillBehavior : IThresholdBehavior
    {
        [DataField]
        public string? Solution;

        /// <summary>
        /// If there is a SpillableComponent on EntityUidowner use it to create a puddle/smear.
        /// Or whatever solution is specified in the behavior itself.
        /// If none are available do nothing.
        /// </summary>
        /// <param name="owner">Entity on which behavior is executed</param>
        /// <param name="system">system calling the behavior</param>
        /// <param name="cause"></param>

        public void Execute(EntityUid owner, DestructibleSystem system, EntityUid? cause = null)
        {
            var solutionContainerSystem = system.EntityManager.System<SharedSolutionContainerSystem>();
            var spillableSystem = system.EntityManager.System<PuddleSystem>();

            var coordinates = system.EntityManager.GetComponent<TransformComponent>(owner).Coordinates;

            if (system.EntityManager.TryGetComponent(owner, out SpillableComponent? spillableComponent) &&
                solutionContainerSystem.TryGetSolution(owner, spillableComponent.SolutionName,
                    out var soln, out var compSolution))
            {
                var spilledSolution = solutionContainerSystem.SplitSolution(
                    soln.Value, compSolution.Volume);

                spillableSystem.TrySplashSpillAt(
                    owner, coordinates, spilledSolution, out _, false, user: cause);
            }
            else if (Solution != null &&
                     solutionContainerSystem.TryGetSolution(owner, Solution,
                         out var behaviorSoln, out var behaviorSolution))
            {
                var spilledSolution = solutionContainerSystem.SplitSolution(
                    behaviorSoln.Value, behaviorSolution.Volume);

                spillableSystem.TrySplashSpillAt(
                    owner, coordinates, spilledSolution, out _, user: cause);
            }
        }

    }
}
