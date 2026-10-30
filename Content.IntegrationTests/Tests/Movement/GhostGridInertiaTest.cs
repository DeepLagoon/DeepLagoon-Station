using System.Numerics;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class GhostGridInertiaTest : InteractionTest
{
    protected override string PlayerPrototype => "AdminObserver";

    [TestCase("AdminObserver", false)]
    [TestCase("AdminObserver", true)]
    [TestCase("MobObserver", false)]
    [TestCase("MobObserver", true)]
    public async Task LeavingMovingGridDoesNotLaunchGhost(string prototype, bool spawnInMapCoordinates)
    {
        await Server.WaitAssertion(() =>
        {
            var physics = SEntMan.System<SharedPhysicsSystem>();
            var grid = MapData.Grid;
            var gridBody = SEntMan.GetComponent<PhysicsComponent>(grid);
            physics.SetBodyType(grid, BodyType.Dynamic, body: gridBody);
            physics.SetFixedRotation(grid, false, body: gridBody);
            physics.SetLinearVelocity(grid, new Vector2(80, 30), body: gridBody);
            physics.SetAngularVelocity(grid, 8f, body: gridBody);

            var coordinates = new EntityCoordinates(grid, 0.5f, 0.5f);
            if (spawnInMapCoordinates)
                coordinates = Transform.WithEntityId(coordinates, MapData.MapUid);
            var ghost = SEntMan.SpawnEntity(prototype, coordinates);
            var body = SEntMan.GetComponent<PhysicsComponent>(ghost);
            Assert.That(body.LinearVelocity, Is.EqualTo(Vector2.Zero), "A newly spawned ghost must start at rest");
            Transform.SetCoordinates(ghost, new EntityCoordinates(MapData.MapUid, new Vector2(-10, -10)));
            Assert.That(body.LinearVelocity, Is.EqualTo(Vector2.Zero), "Grid momentum must not propel a ghost");
            Assert.That(body.AngularVelocity, Is.Zero);
            SEntMan.DeleteEntity(ghost);
        });
    }
}
