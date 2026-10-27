using Robust.Shared.GameObjects;
using Content.Server.Singularity.EntitySystems;
using Content.Shared.Singularity.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics;

namespace Content.IntegrationTests.Tests.Singularity;

[TestFixture]
public sealed class EventHorizonTests
{
    [TestCase(-0.5f)]
    [TestCase(0f)]
    [TestCase(2f)]
    public async Task DisabledRadiusRemainsSemanticWhileFixturesStayValid(float radius)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var singularity = entities.SpawnEntity("Singularity", map.GridCoords);
            var horizons = entities.System<EventHorizonSystem>();
            horizons.SetRadius(singularity, radius);
            var horizon = entities.GetComponent<EventHorizonComponent>(singularity);
            Assert.That(horizon.Radius, Is.EqualTo(radius));
            var fixtures = entities.GetComponent<FixturesComponent>(singularity);
            foreach (var id in new[] { horizon.ConsumerFixtureId, horizon.ColliderFixtureId })
            {
                var circle = (PhysShapeCircle) fixtures.Fixtures[id!].Shape;
                Assert.That(circle.Radius, Is.EqualTo(Math.Max(0f, radius)));
            }
            horizons.SetRadius(singularity, 2f);
            Assert.That(((PhysShapeCircle) fixtures.Fixtures[horizon.ConsumerFixtureId!].Shape).Radius, Is.EqualTo(2f));
        });
        await pair.CleanReturnAsync();
    }
}
