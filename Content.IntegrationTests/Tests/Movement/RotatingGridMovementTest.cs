using System.Numerics;
using Content.Shared.Movement.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class RotatingGridMovementTest : MovementTest
{
    protected override int Tiles => 16;
    protected override bool AddWalls => false;

    [TestCase(0f)]
    [TestCase(8f)]
    [TestCase(-8f)]
    public async Task WalkingDoesNotDriftSideways(float angularVelocity)
    {
        Vector2 start = default;
        await Server.WaitPost(() =>
        {
            var grid = MapData.Grid;
            for (var x = -16; x <= 16; x++)
            for (var y = -4; y <= 4; y++)
                MapSystem.SetTile(grid, new Vector2i(x, y), new Tile(TileMan[Plating].TileId));
            var physics = SEntMan.System<SharedPhysicsSystem>();
            var body = SEntMan.GetComponent<PhysicsComponent>(grid);
            physics.SetBodyType(grid, BodyType.Dynamic, body: body);
            physics.SetFixedRotation(grid, false, body: body);
            physics.SetLinearDamping(grid, body, 0f);
            physics.SetAngularDamping(grid, body, 0f);
            physics.SetLinearVelocity(grid, new Vector2(40, 15), body: body);
            physics.SetAngularVelocity(grid, angularVelocity, body: body);
            start = SEntMan.GetComponent<TransformComponent>(SPlayer).LocalPosition;
        });
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        await RunSeconds(1f);
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Up);
        await Server.WaitAssertion(() =>
        {
            var xform = SEntMan.GetComponent<TransformComponent>(SPlayer);
            Assert.That(xform.ParentUid, Is.EqualTo(MapData.Grid.Owner));
            Assert.That(xform.LocalPosition.X, Is.GreaterThan(start.X + 1f));
            Assert.That(xform.LocalPosition.Y, Is.EqualTo(start.Y).Within(0.05f));
        });
        await Client.WaitAssertion(() =>
        {
            var xform = CEntMan.GetComponent<TransformComponent>(CPlayer);
            Assert.That(xform.LocalPosition.Y, Is.EqualTo(start.Y).Within(0.05f),
                "Client prediction must agree with movement along the deck");
        });
    }
}
