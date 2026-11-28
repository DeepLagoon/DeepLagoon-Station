using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds.Behaviors;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class DestructibleSpillTests
{
    [Test]
    public async Task SpillBehaviorTwicePreserves50uAndEmptiesBeaker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var solutions = entities.System<SharedSolutionContainerSystem>();
            var beaker = entities.SpawnEntity("Beaker", map.GridCoords);
            Assert.That(solutions.TryGetDrainableSolution(beaker, out var source, out var contents), Is.True);
            Assert.That(solutions.TryAddReagent(source!.Value, "Water", 50), Is.True);
            Assert.That(contents!.Volume, Is.EqualTo(FixedPoint2.New(50)));
            var spill = new SpillBehavior();
            var destructible = entities.System<DestructibleSystem>();
            for (var execution = 1; execution <= 2; execution++)
            {
                spill.Execute(beaker, destructible);
                Assert.That(contents.Volume, Is.EqualTo(FixedPoint2.Zero),
                    $"Spill execution {execution} must consume its source rather than copy the solution.");
                var total = FixedPoint2.Zero;
                var puddles = entities.EntityQueryEnumerator<PuddleComponent, TransformComponent>();
                while (puddles.MoveNext(out var uid, out _, out var transform))
                {
                    if (transform.MapID != map.MapId) continue;
                    Assert.That(solutions.TryGetSolution(uid, "puddle", out _, out var liquid), Is.True);
                    total += liquid!.Volume;
                }
                Assert.That(total, Is.EqualTo(FixedPoint2.New(50)),
                    $"Spill execution {execution} must preserve the original 50u without duplicating liquid.");
                Assert.That(entities.EntityExists(beaker), Is.True, "Exercise repeated spill behavior before entity deletion.");
            }
        });
        await pair.CleanReturnAsync();
    }
}
