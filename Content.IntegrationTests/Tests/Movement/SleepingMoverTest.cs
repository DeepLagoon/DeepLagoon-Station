using System;
using System.Numerics;
using System.Reflection;
using Content.Server.Physics.Controllers;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.Movement;

[TestFixture, NonParallelizable]
public sealed class SleepingMoverTest
{
    [Test]
    public async Task IdleBodySkipsPreparationButInputAndExternalMotionStillRun()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var controller = em.System<MoverController>();
            var uid = em.SpawnEntity(null, map.GridCoords);
            var mover = em.AddComponent<InputMoverComponent>(uid);
            var body = em.AddComponent<PhysicsComponent>(uid);
            var physics = em.System<SharedPhysicsSystem>();
            physics.SetBodyType(uid, BodyType.KinematicController, body: body);
            physics.SetBodyStatus(uid, body, BodyStatus.OnGround);
            Assert.That(em.System<FixtureSystem>().TryCreateFixture(uid, new PhysShapeCircle(.1f), "body",
                hard: true, collisionLayer: 1, body: body), Is.True);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(SharedMoverController);
            var capture = type.GetField("CaptureMoverBranches", flags)!;
            var counters = type.GetField("MoverBranches", flags)!;
            var run = type.GetMethod("HandleMobMovement", flags)!;
            void Run() => run.Invoke(controller, [new Entity<InputMoverComponent>(uid, mover), 1f / 60f]);
            long Count(string name) => (long)counters.FieldType.GetField(name)!.GetValue(counters.GetValue(controller))!;
            var oldCapture = capture.GetValue(controller);
            var oldCounters = counters.GetValue(controller);
            try
            {
                capture.SetValue(controller, true);
                counters.SetValue(controller, Activator.CreateInstance(counters.FieldType));
                physics.SetAwake(uid, body, false);
                var position = em.GetComponent<TransformComponent>(uid).LocalPosition;
                Run();
                Assert.That(Count("SleepingIdle"), Is.EqualTo(1));
                Assert.That(Count("Prepared"), Is.Zero);
                Assert.That(body.Awake, Is.False);
                Assert.That(em.GetComponent<TransformComponent>(uid).LocalPosition, Is.EqualTo(position));

                mover.HeldMoveButtons = MoveButtons.Right;
                Run();
                Assert.That(Count("Prepared"), Is.EqualTo(1));
                Assert.That(body.LinearVelocity.X, Is.GreaterThan(0));
                Assert.That(body.Awake, Is.True);

                mover.HeldMoveButtons = MoveButtons.None;
                mover.WishDir = Vector2.Zero;
                physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
                em.System<SharedTransformSystem>().SetLocalRotation(map.Grid, Angle.FromDegrees(90));
                physics.SetAwake(uid, body, false);
                Run();
                Assert.That(mover.GridVelocityRotation, Is.Null,
                    "A sleeping zero-velocity body must not keep a stale grid frame for a future impulse.");
                physics.SetLinearVelocity(uid, new Vector2(2, 0), body: body);
                Run();
                Assert.That(Count("Prepared"), Is.EqualTo(2), "Externally moving bodies must still apply friction.");
                Assert.That(body.LinearVelocity.X, Is.GreaterThan(0));
                Assert.That(body.LinearVelocity.Y, Is.EqualTo(0).Within(.001f),
                    "Grid rotation while asleep must not turn a newly applied external velocity.");

                physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
                physics.SetAwake(uid, body, false);
                mover.WishDir = Vector2.Zero;
                mover.LastInputTick = pair.Server.ResolveDependency<IGameTiming>().CurTick;
                mover.CurTickWalkMovement = new Vector2(.5f, 0);
                Run();
                Assert.That(Count("Prepared"), Is.EqualTo(3), "A released sub-tick input must not be discarded.");
            }
            finally
            {
                capture.SetValue(controller, oldCapture);
                counters.SetValue(controller, oldCounters);
            }
        });
        await pair.CleanReturnAsync();
    }
}
