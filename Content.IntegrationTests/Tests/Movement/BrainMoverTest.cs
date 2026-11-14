using System.Numerics;
using Content.Shared.Mind;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

[TestFixture]
public sealed class BrainMoverTest
{
    [Test]
    public async Task InertBrainResumesUpdatesWhenItGainsAMindOrRelay()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var server = pair.Server;
        var entities = server.EntMan;
        EntityUid map = default;
        EntityUid brain = default;
        EntityUid relayBrain = default;
        EntityUid relayTarget = default;
        var relayRotation = Angle.FromDegrees(45);

        await server.WaitAssertion(() =>
        {
            map = entities.System<SharedMapSystem>().CreateMap();
            var coordinates = new EntityCoordinates(map, Vector2.Zero);
            brain = entities.SpawnEntity("OrganHumanBrain", coordinates);
            relayBrain = entities.SpawnEntity("OrganAnimalBrain", coordinates);
            Assert.That(entities.GetComponent<InputMoverComponent>(brain).CanMove, Is.False);
            Assert.That(entities.GetComponent<InputMoverComponent>(relayBrain).CanMove, Is.False);
            entities.GetComponent<InputMoverComponent>(brain).TargetRelativeRotation = relayRotation;
            entities.GetComponent<InputMoverComponent>(relayBrain).TargetRelativeRotation = relayRotation;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<ActiveInputMoverComponent>(brain), Is.True);
            Assert.That(entities.GetComponent<InputMoverComponent>(brain).RelativeRotation, Is.EqualTo(Angle.Zero));
            Assert.That(entities.GetComponent<InputMoverComponent>(relayBrain).RelativeRotation, Is.EqualTo(Angle.Zero));

            var minds = entities.System<SharedMindSystem>();
            var mind = minds.CreateMind(null);
            minds.TransferTo(mind, brain, mind: mind);
            relayTarget = entities.SpawnEntity("MobHuman", new EntityCoordinates(map, Vector2.Zero));
            entities.System<SharedMoverController>().SetRelay(relayBrain, relayTarget);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<InputMoverComponent>(brain).RelativeRotation, Is.Not.EqualTo(Angle.Zero));
            Assert.That(entities.GetComponent<InputMoverComponent>(relayTarget).RelativeRotation, Is.Not.EqualTo(Angle.Zero));
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
