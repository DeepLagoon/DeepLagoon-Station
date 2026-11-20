using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._Mono.NPC.HTN;
using Content.Server.Physics.Controllers;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared._Mono.Weapons.Ranged.Components;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Spawners;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShipSteeringProjectileLookupTests
{
    [Test]
    public async Task SteeringKeepsNativeProjectileCandidatesAcrossMovementDeletionAndDisabledAvoidance()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var transform = em.System<SharedTransformSystem>();
            var physics = em.System<SharedPhysicsSystem>();
            var fixtures = em.System<FixtureSystem>();
            var lookup = em.System<EntityLookupSystem>();
            var system = em.System<ShipSteeringSystem>();
            var ship = map.Grid.Owner;
            var shuttle = em.EnsureComponent<ShuttleComponent>(ship);
            // This fixture exercises the full input handler; finite thrust avoids the unrelated
            // zero-acceleration intercept calculation of a newly-created empty test grid.
            Array.Fill(shuttle.LinearThrust, 1000f);
            Array.Fill(shuttle.BaseLinearThrust, 1000f);
            Array.Fill(shuttle.RatedBaseLinearThrust, 1000f);
            Array.Fill(shuttle.AccelerationLinearThrust, 1000f);
            Array.Fill(shuttle.AccelerationRatedBaseLinearThrust, 1000f);
            shuttle.AngularThrust = 1000f;
            var owner = em.SpawnEntity(null, map.GridCoords);
            var steering = em.AddComponent<ShipSteererComponent>(owner);
            steering.Coordinates = new EntityCoordinates(map.MapUid, new Vector2(100, 0));
            steering.AvoidCollisions = false;
            steering.AvoidProjectiles = true;
            steering.NoFinish = true;

            EntityUid SpawnProjectile(Vector2 position, bool sensor)
            {
                var uid = em.SpawnEntity(null, new EntityCoordinates(map.MapUid, position));
                em.AddComponent<ShipWeaponProjectileComponent>(uid);
                var body = em.EnsureComponent<PhysicsComponent>(uid);
                physics.SetBodyType(uid, BodyType.Dynamic, body: body);
                Assert.That(fixtures.TryCreateFixture(uid, new PhysShapeCircle(.2f), "projectile", hard: !sensor,
                    collisionLayer: (int) CollisionGroup.BulletImpassable, body: body), Is.True);
                physics.SetCanCollide(uid, true, body: body);
                return uid;
            }

            var dynamicProjectile = SpawnProjectile(new Vector2(20, 0), sensor: false);
            var sensorProjectile = SpawnProjectile(new Vector2(30, 0), sensor: true);
            var ownProjectile = SpawnProjectile(new Vector2(40, 0), sensor: false);
            em.AddComponent<ProjectileGridPhaseComponent>(ownProjectile).SourceGrid = ship;
            var distantProjectile = SpawnProjectile(new Vector2(2000, 0), sensor: false);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var bufferField = typeof(ShipSteeringSystem).GetField("_avoidProjs", flags)!;
            var candidatesField = typeof(ShipSteeringSystem).GetField("_avoidPotentialEnts", flags)!;
            var buffer = (HashSet<Entity<ShipWeaponProjectileComponent>>) bufferField.GetValue(system)!;

            EntityUid[] Scan()
            {
                var inputs = new GetShuttleInputsEvent(1f / 60f, ship);
                em.EventBus.RaiseLocalEvent(owner, ref inputs);
                Assert.That(inputs.GotInput, Is.True, "The normal ship input handler must perform this scan.");
                Assert.That(bufferField.GetValue(system), Is.SameAs(buffer));

                // Compare against the allocating engine overload, not a second copy of steering's query/filter code.
                var expected = lookup.GetEntitiesInRange<ShipWeaponProjectileComponent>(
                    transform.GetMapCoordinates(ship), steering.ProjectileSearchBounds,
                    LookupFlags.Approximate | LookupFlags.Dynamic | LookupFlags.Sensors);
                Assert.That(buffer.Select(e => e.Owner), Is.EquivalentTo(
                    steering.AvoidProjectiles ? expected.Select(e => e.Owner) : Enumerable.Empty<EntityUid>()));
                return ((List<(EntityUid Uid, bool IsGrid)>) candidatesField.GetValue(system)!).Select(e => e.Uid).ToArray();
            }

            var first = Scan();
            Assert.That(first, Does.Contain(dynamicProjectile));
            Assert.That(first, Does.Contain(sensorProjectile), "Sensors are part of the native avoidance query.");
            Assert.That(first, Does.Not.Contain(ownProjectile), "The source-grid filter still excludes our own shot.");
            Assert.That(first, Does.Not.Contain(distantProjectile));
            Assert.That(buffer.Select(e => e.Owner), Does.Contain(ownProjectile), "Own shots are filtered after lookup.");

            transform.SetWorldPosition(dynamicProjectile, new Vector2(2000, 20));
            transform.SetWorldPosition(distantProjectile, new Vector2(50, 0));
            var moved = Scan();
            Assert.That(moved, Does.Not.Contain(dynamicProjectile));
            Assert.That(moved, Does.Contain(distantProjectile));

            em.DeleteEntity(sensorProjectile);
            Assert.That(Scan(), Does.Not.Contain(sensorProjectile), "A reused query buffer must not retain a deleted shot.");

            steering.AvoidProjectiles = false;
            Assert.That(Scan(), Is.Empty, "Turning avoidance off clears the previous scan's candidates.");
            steering.AvoidProjectiles = true;
            Assert.That(Scan(), Does.Contain(distantProjectile), "Re-enabling uses the current native broadphase.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeSteeringPreservesNavigationAndAvoidsLiveMovingObstaclesButNotExpiredShots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var transform = em.System<SharedTransformSystem>();
            var physics = em.System<SharedPhysicsSystem>();
            var fixtures = em.System<FixtureSystem>();
            var system = em.System<ShipSteeringSystem>();
            var ship = map.Grid.Owner;
            var shuttle = em.EnsureComponent<ShuttleComponent>(ship);
            Array.Fill(shuttle.LinearThrust, 1000f);
            Array.Fill(shuttle.BaseLinearThrust, 1000f);
            Array.Fill(shuttle.RatedBaseLinearThrust, 1000f);
            Array.Fill(shuttle.AccelerationLinearThrust, 1000f);
            Array.Fill(shuttle.AccelerationRatedBaseLinearThrust, 1000f);
            shuttle.AngularThrust = 1000f;
            var owner = em.SpawnEntity(null, map.GridCoords);
            var steering = em.AddComponent<ShipSteererComponent>(owner);
            steering.Coordinates = new EntityCoordinates(map.MapUid, new Vector2(100, 0));
            steering.AvoidCollisions = false;
            steering.NoFinish = true;
            steering.RotationCompensationGain = 0;

            ShuttleInput Input(bool avoid)
            {
                steering.AvoidProjectiles = avoid;
                var inputs = new GetShuttleInputsEvent(1f / 60f, ship);
                em.EventBus.RaiseLocalEvent(owner, ref inputs);
                Assert.That(inputs.GotInput, Is.True);
                Assert.That(inputs.Input, Is.Not.Null);
                return inputs.Input!.Value;
            }

            // The native input with no avoidance is the navigation reference, not a
            // second implementation of steering or its collision prediction.
            var clear = Input(avoid: false);
            Assert.That(clear.Strafe.LengthSquared(), Is.GreaterThan(0));
            Assert.That(Input(avoid: true), Is.EqualTo(clear), "Empty space preserves the entire native movement input.");

            var projectile = em.SpawnEntity(null, new EntityCoordinates(map.MapUid, new Vector2(10, 0)));
            em.AddComponent<ShipWeaponProjectileComponent>(projectile);
            var body = em.EnsureComponent<PhysicsComponent>(projectile);
            physics.SetBodyType(projectile, BodyType.Dynamic, body: body);
            Assert.That(fixtures.TryCreateFixture(projectile, new PhysShapeCircle(2f), "obstacle", hard: true,
                collisionLayer: (int) CollisionGroup.BulletImpassable, body: body), Is.True);
            physics.SetCanCollide(projectile, true, body: body);
            Assert.That(Input(avoid: true), Is.Not.EqualTo(clear), "A blocking shot still forces avoidance.");

            physics.SetLinearVelocity(projectile, new Vector2(-5, 0), body: body);
            Assert.That(Input(avoid: true), Is.Not.EqualTo(clear), "The same shot moving towards us is still avoided.");
            transform.SetWorldPosition(projectile, new Vector2(10, 100));
            physics.SetLinearVelocity(projectile, Vector2.Zero, body: body);
            Assert.That(Input(avoid: true), Is.EqualTo(clear), "A nearby shot outside the desired trajectory preserves navigation.");

            transform.SetWorldPosition(projectile, new Vector2(10, 0));
            var despawn = em.AddComponent<TimedDespawnComponent>(projectile);
            despawn.Lifetime = -1;
            Assert.That(Input(avoid: true), Is.EqualTo(clear), "A shot that despawns before impact must not change the desired input.");
            despawn.Lifetime = 60;
            Assert.That(Input(avoid: true), Is.Not.EqualTo(clear), "A live shot at the same location is still avoided.");
            em.DeleteEntity(projectile);
            Assert.That(Input(avoid: true), Is.EqualTo(clear), "Deleting the last obstacle restores navigation immediately.");
        });
        await pair.CleanReturnAsync();
    }

}
