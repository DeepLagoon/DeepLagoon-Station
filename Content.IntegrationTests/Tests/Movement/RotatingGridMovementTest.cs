using System.Numerics;
using System.Reflection;
using Robust.Client.GameObjects;
using Robust.Client.GameStates;
using Robust.Shared;
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

    [TestCase(0f, 1f)]
    [TestCase(12f, 0f)]
    [TestCase(-12f, 0f)]
    public async Task ReconciliationResumesOffTheMovingDeck(float speed, float angularSpeed)
    {
        await RunTicks(10);
        await Client.WaitAssertion(() =>
        {
            var physics = CEntMan.System<SharedPhysicsSystem>();
            var player = CEntMan.GetComponent<TransformComponent>(CPlayer);
            var body = CEntMan.GetComponent<PhysicsComponent>(CPlayer);
            var grid = player.ParentUid;
            EntityUid Target()
            {
                var query = new GetPredictionReconciliationTargetEvent(CPlayer);
                CEntMan.EventBus.RaiseLocalEvent(CPlayer, ref query);
                return query.Target;
            }

            physics.SetAngularVelocity(grid, 0f);
            physics.SetLinearVelocity(grid, Vector2.Zero);
            Assert.That(Target(), Is.EqualTo(CPlayer));
            physics.SetAngularVelocity(grid, angularSpeed);
            physics.SetLinearVelocity(grid, new Vector2(speed, 0));
            Assert.That(Target().IsValid(), Is.False);
            physics.SetBodyStatus(CPlayer, body, BodyStatus.InAir);
            Assert.That(Target(), Is.EqualTo(CPlayer));
            physics.SetBodyStatus(CPlayer, body, BodyStatus.OnGround);
            Assert.That(Target().IsValid(), Is.False);
            physics.SetAngularVelocity(grid, 0f);
            physics.SetLinearVelocity(grid, Vector2.Zero);
            Assert.That(Target(), Is.EqualTo(CPlayer));
            Assert.That(body.Predict, Is.True);
        });
    }

    [TestCase(0.02f, 0f)]
    [TestCase(-0.02f, 0f)]
    [TestCase(0f, 12f)]
    [TestCase(0f, -12f)]
    [TestCase(0f, 100f)]
    [TestCase(0f, -100f)]
    public async Task ReconciliationDoesNotDragPlayerAcrossTheDeck(float rotation, float speed)
    {
        await RunTicks(10);
        await Client.WaitAssertion(() =>
        {
            var transforms = CEntMan.System<TransformSystem>();
            var physics = CEntMan.System<SharedPhysicsSystem>();
            var player = CEntMan.GetComponent<TransformComponent>(CPlayer);
            var grid = player.ParentUid;
            var start = new Vector2(10.5f, 0.5f);
            transforms.SetLocalPosition(CPlayer, start);
            physics.SetAngularVelocity(grid, rotation * 30);
            physics.SetLinearVelocity(grid, new Vector2(speed, 0));
            transforms.ResetRenderTransforms();

            var query = new GetPredictionReconciliationTargetEvent(CPlayer);
            CEntMan.EventBus.RaiseLocalEvent(CPlayer, ref query);
            if (query.Target.IsValid())
            {
                InvokePrediction("RecordPredictionSample", transforms, CPlayer, CTiming.CurTick, 0u);
                InvokePrediction("BeginPredictionRollback", transforms, CPlayer, CTiming.CurTick);
            }

            var gridTransform = CEntMan.GetComponent<TransformComponent>(grid);
            var translation = speed == 0 ? 0.1f : speed * TickPeriod;
            transforms.SetLocalPositionRotation(grid, gridTransform.LocalPosition + new Vector2(translation, 0),
                gridTransform.LocalRotation + new Angle(rotation));
            transforms.SnapRenderTransform(grid);
            transforms.SetLocalPosition(CPlayer, start + new Vector2(0.1f, 0));
            InvokePrediction("CompletePredictionRollback", transforms, CTiming.CurTick);
            InvokePrediction("FinishPredictionRollback", transforms);

            var deck = transforms.GetRenderWorldTransform(grid);
            var local = (-deck.Rotation).RotateVec(transforms.GetRenderWorldPosition(CPlayer) - deck.Position);
            Assert.That(local.Y, Is.EqualTo(start.Y).Within(0.001f),
                "A server update must not pull the rendered player sideways across the moving deck.");
            Assert.That(local.X, Is.InRange(start.X - 0.001f, start.X + 0.101f),
                "Grid translation must not become a forward/backward player correction.");
            Assert.That(CEntMan.GetComponent<PhysicsComponent>(CPlayer).Predict, Is.True);
            Assert.That(Client.ResolveDependency<IClientGameStateManager>().IsPredictionEnabled, Is.True);
        });
    }

    // Drive a packet/replay boundary without depending on wall-clock network latency.
    // Reflection is confined to the test assembly; production uses the public query event.
    private static void InvokePrediction(string name, TransformSystem transforms, params object[] args)
    {
        typeof(TransformSystem).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(transforms, args);
    }

    [TestCase(0f, 0)]
    [TestCase(8f, 0)]
    [TestCase(-8f, 0)]
    [TestCase(0f, 8)]
    [TestCase(8f, 8)]
    [TestCase(-8f, 8)]
    public async Task WalkingDoesNotDriftSideways(float angularVelocity, int predictionTicks)
    {
        await Client.WaitPost(() => Client.CfgMan.SetCVar(CVars.NetPredictTickBias, predictionTicks));
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
            SEntMan.SpawnEntity("WallSolid", new EntityCoordinates(grid, 13.5f, 0.5f));
            // Exercise the deck away from the centre of rotation as on a real shuttle.
            start = new Vector2(10.5f, 0.5f);
            Transform.SetLocalPosition(SPlayer, start);
        });
        await RunTicks(10);
        await SetKey(EngineKeyFunctions.MoveRight, BoundKeyState.Down);
        for (var tick = 0; tick < 30; tick++)
        {
            await RunTicks(1);
            await Client.WaitAssertion(() =>
            {
                var xform = CEntMan.GetComponent<TransformComponent>(CPlayer);
                Assert.That(xform.LocalPosition.Y, Is.EqualTo(start.Y).Within(0.05f),
                    $"Prediction must remain on the deck at tick {tick}, not just after reconciliation");
                var transforms = CEntMan.System<TransformSystem>();
                transforms.FrameUpdate(TickPeriod);
                var gridPose = transforms.GetRenderWorldTransform(xform.ParentUid);
                var renderedLocal = (-gridPose.Rotation).RotateVec(
                    transforms.GetRenderWorldPosition(CPlayer) - gridPose.Position);
                Assert.That(renderedLocal.Y, Is.EqualTo(start.Y).Within(0.05f),
                    $"Rendered prediction must remain on the deck at tick {tick}");
                Assert.That(renderedLocal.X, Is.InRange(start.X - 0.05f, 13.05f),
                    $"Rendered prediction must not cross the wall at tick {tick}");
            });
        }
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
