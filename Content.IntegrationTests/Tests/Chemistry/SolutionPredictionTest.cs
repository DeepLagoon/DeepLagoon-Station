using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Robust.Client.GameObjects;
using Robust.Client.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.IntegrationTests.Tests.Chemistry;

public sealed class SolutionPredictionTest : InteractionTest
{
    [TestCase("Beaker", "beaker3")]
    [TestCase("DrinkGlass", "fill4")]
    public async Task ReplayingOnePourKeepsContainerHalfFull(string prototype, string fillState)
    {
        var sourceNet = await PlaceInHands("LargeBeaker");
        var targetNet = await SpawnTarget(prototype);
        await Server.WaitAssertion(() =>
        {
            var solutions = SEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetDrainableSolution(ToServer(sourceNet), out var source, out _), Is.True);
            Assert.That(solutions.TryAddReagent(source!.Value, "Water", 60), Is.True);
        });
        await RunTicks(8);

        await Client.WaitAssertion(() =>
        {
            var source = ToClient(sourceNet);
            var target = ToClient(targetNet);
            var solutions = CEntMan.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetDrainableSolution(source, out var sourceSolution, out _), Is.True);
            Assert.That(solutions.TryGetRefillableSolution(target, out var targetSolution, out _), Is.True);
            var sourceEntity = sourceSolution!.Value;
            var targetEntity = targetSolution!.Value;
            var netId = Client.ResolveDependency<IComponentFactory>().GetRegistration<SolutionComponent>().NetID!.Value;
            var states = Client.ResolveDependency<IClientGameStateManager>().GetFullRep();
            var sourceState = (SolutionComponent.SolutionComponent_AutoState) states[CEntMan.GetNetEntity(sourceEntity)][netId]!;
            var targetState = (SolutionComponent.SolutionComponent_AutoState) states[CEntMan.GetNetEntity(targetEntity)][netId]!;
            var amount = targetState.Solution.MaxVolume / 2;
            var transfer = CEntMan.System<SolutionTransferSystem>();
            var appearance = CEntMan.System<AppearanceSystem>();
            var sprite = CEntMan.GetComponent<SpriteComponent>(target);

            // Reconcile against the cached states and replay the same unacknowledged input twice.
            for (var replay = 0; replay < 2; replay++)
            {
                var sourceReset = new ComponentHandleState(sourceState, null);
                var targetReset = new ComponentHandleState(targetState, null);
                CEntMan.EventBus.RaiseComponentEvent(sourceEntity, sourceEntity.Comp, ref sourceReset);
                CEntMan.EventBus.RaiseComponentEvent(targetEntity, targetEntity.Comp, ref targetReset);

                Assert.That(transfer.Transfer(CPlayer, source, sourceEntity, target, targetEntity, amount), Is.EqualTo(amount));
                appearance.OnChangeData(target, sprite);
                Assert.That(appearance.TryGetData<float>(target, SolutionContainerVisuals.FillFraction, out var fraction), Is.True);
                Assert.That(fraction, Is.EqualTo(0.5f));
                Assert.That(sprite[sprite.LayerMapGet(SolutionContainerLayers.Fill)].RsiState.Name, Is.EqualTo(fillState));
            }

            Assert.That(sourceState.Solution.Volume, Is.EqualTo(FixedPoint2.New(60)));
            Assert.That(targetState.Solution.Volume, Is.EqualTo(FixedPoint2.Zero));
        });
    }
}
