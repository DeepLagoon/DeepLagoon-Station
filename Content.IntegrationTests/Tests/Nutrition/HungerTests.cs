using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Nutrition;

[TestFixture]
public sealed class HungerTests
{
    [TestCase(HungerThreshold.Overfed, HungerThreshold.Okay)]
    [TestCase(HungerThreshold.Okay, HungerThreshold.Peckish)]
    [TestCase(HungerThreshold.Peckish, HungerThreshold.Starving)]
    public async Task DecayRateChangePreservesHunger(HungerThreshold previous, HungerThreshold next)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<HungerSystem>();
            var uid = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var hunger = entities.AddComponent<HungerComponent>(uid);
            var boundary = hunger.Thresholds[next];
            var previousRate = hunger.BaseDecayRate * hunger.HungerThresholdDecayModifiers[previous];
            system.SetHunger(uid, boundary + previousRate * 5, hunger);
            Assert.That(hunger.CurrentThreshold, Is.EqualTo(previous));

#pragma warning disable RA0002 // Simulate elapsed time since the last authoritative hunger update.
            hunger.LastAuthoritativeHungerChangeTime -= TimeSpan.FromSeconds(10);
#pragma warning restore RA0002
            var before = system.GetHunger(hunger);
            Assert.That(before, Is.LessThan(boundary));

            system.Update(0);
            Assert.That(hunger.CurrentThreshold, Is.EqualTo(next));
            Assert.That(system.GetHunger(hunger), Is.EqualTo(before).Within(0.00001f),
                "Crossing a threshold must not retroactively apply its decay rate to elapsed time.");
            Assert.That(hunger.ActualDecayRate,
                Is.EqualTo(hunger.BaseDecayRate * hunger.HungerThresholdDecayModifiers[next]));

#pragma warning disable RA0002 // Force another threshold check after one second at the new rate.
            hunger.LastAuthoritativeHungerChangeTime -= TimeSpan.FromSeconds(1);
            hunger.NextThresholdUpdateTime = TimeSpan.Zero;
#pragma warning restore RA0002
            system.Update(0);
            Assert.That(hunger.CurrentThreshold, Is.EqualTo(next),
                "Natural hunger decay must not switch back to the previous mood message.");
            Assert.That(system.GetHunger(hunger), Is.EqualTo(before - hunger.ActualDecayRate).Within(0.00001f));
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }
}
