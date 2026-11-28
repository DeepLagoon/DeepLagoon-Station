using System;
using System.Numerics;
using System.Reflection;
using Content.Server.Physics.Controllers;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MoverBranchCounterTests
{
    [Test]
    public async Task CountersFollowActualNativeBranchesAndDoNotChangeMovement()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var controller = em.System<MoverController>();
            Entity<InputMoverComponent> Spawn()
            {
                var uid = em.SpawnEntity(null, map.GridCoords);
                return (uid, em.AddComponent<InputMoverComponent>(uid));
            }
            var blocked = Spawn();
            blocked.Comp.CanMove = false;
            var noPhysics = Spawn();
            var prepared = Spawn();
            var body = em.AddComponent<PhysicsComponent>(prepared);
            var physics = em.System<SharedPhysicsSystem>();
            physics.SetBodyType(prepared, BodyType.KinematicController, body: body);
            physics.SetBodyStatus(prepared, body, BodyStatus.OnGround);
            Assert.That(em.System<FixtureSystem>().TryCreateFixture(prepared, new PhysShapeCircle(.1f), "body",
                hard: true, collisionLayer: 1, body: body), Is.True);
            physics.WakeBody(prepared, body: body);
            var xform = em.GetComponent<TransformComponent>(prepared);
            var originalPosition = xform.LocalPosition;
            var originalRotation = xform.LocalRotation;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var baseType = typeof(SharedMoverController);
            var capture = baseType.GetField("CaptureMoverBranches", flags)!;
            var counters = baseType.GetField("MoverBranches", flags)!;
            var run = baseType.GetMethod("HandleMobMovement", flags)!;
            void Run(Entity<InputMoverComponent> entity) => run.Invoke(controller, [entity, 1f / 60f]);
            long Count(string name) => (long) counters.FieldType.GetField(name)!.GetValue(counters.GetValue(controller))!;
            var previousCapture = capture.GetValue(controller);
            var previousCounters = counters.GetValue(controller);
            try
            {
                counters.SetValue(controller, Activator.CreateInstance(counters.FieldType));
                capture.SetValue(controller, false);
                Run(prepared);
                var velocityWithoutCounters = body.LinearVelocity;
                var wishWithoutCounters = prepared.Comp.WishDir;
                Assert.That(Count("Entered"), Is.Zero, "Disabled counters must remain untouched.");

                capture.SetValue(controller, true);
                Run(blocked);
                Run(noPhysics);
                Run(prepared);
                Assert.Multiple(() =>
                {
                    Assert.That(Count("Entered"), Is.EqualTo(3));
                    Assert.That(Count("CannotMove"), Is.EqualTo(1));
                    Assert.That(Count("NoPhysics"), Is.EqualTo(1));
                    Assert.That(Count("Prepared"), Is.EqualTo(1));
                    Assert.That(Count("Serial"), Is.EqualTo(1));
                    Assert.That(Count("Queued"), Is.Zero);
                    Assert.That(Count("ZeroWish"), Is.EqualTo(1));
                    Assert.That(Count("Contained"), Is.Zero);
                    Assert.That(body.LinearVelocity, Is.EqualTo(velocityWithoutCounters));
                    Assert.That(prepared.Comp.WishDir, Is.EqualTo(wishWithoutCounters));
                    Assert.That(xform.LocalPosition, Is.EqualTo(originalPosition));
                    Assert.That(xform.LocalRotation, Is.EqualTo(originalRotation));
                    Assert.That(controller.UsedMobMovement[blocked], Is.False);
                    Assert.That(controller.UsedMobMovement[prepared], Is.True);
                });
            }
            finally
            {
                capture.SetValue(controller, previousCapture);
                counters.SetValue(controller, previousCounters);
            }
        });
        await pair.CleanReturnAsync();
    }
}
