using System;
using System.Numerics;
using System.Collections.Generic;
using Content.Client.Eye;
using Content.Server.Construction;
using Content.Server.Mech.Systems;
using Content.Server.Physics.Controllers;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Damage;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ShuttleMotionRegressionTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: DLFlightLimitThrusterProbe
  components:
  - type: Thruster
    baseThrust: 100
    thrust: 100
    requireSpace: false
    burnShape: []
";

    [Test]
    public async Task S2AndS4WeightlessSprintSpeedsFitTheLightShuttleLimit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var spawned = new List<(EntityUid Mech, float TargetSpeed)>();
        EntityUid space = default;
        var shuttleLimit = ShuttleFlightLimits.MaxSpeed(1f, 50f);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var mechs = entities.System<MechSystem>();
            var mover = entities.System<MoverController>();
            space = entities.System<SharedMapSystem>().CreateMap(out _);
            foreach (var (model, sprintSpeed) in new[]
            {
                ("AFInterceptor", 39.9f),
                ("AFInterceptorM", 33.25f),
                ("AFFlail", 25f),
                ("AFFlailE", 18f),
                ("AFMace", 45f),
            })
            {
                foreach (var suffix in new[] { "", "TSF", "PDV" })
                {
                    var prototype = prototypes.Index(new EntProtoId(model + suffix));
                    Assert.That(prototype.TryComp<MovementSpeedModifierComponent>(out var movement, entities.ComponentFactory), Is.True);
                    Assert.That(movement!.BaseSprintSpeed * movement.BaseWeightlessModifier,
                        Is.EqualTo(sprintSpeed).Within(0.001f), prototype.ID);
                }

                var position = new EntityCoordinates(space, spawned.Count * 10f, 0f);
                var mech = entities.SpawnEntity(new EntProtoId(model), position);
                mechs.InsertBattery(mech, entities.SpawnEntity("PowerCageHigh", position));
                Assert.That(mechs.TryInsert(mech, entities.SpawnEntity("MobHuman", position)), Is.True, model);
                mover.SetVelocityDirection((mech, entities.GetComponent<InputMoverComponent>(mech)), Direction.North, 0, true);
                spawned.Add((mech, sprintSpeed));
            }
        });
        await pair.RunTicksSync(300);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var (mech, targetSpeed) in spawned)
            {
                var actual = entities.GetComponent<PhysicsComponent>(mech).LinearVelocity.Length();
                var id = entities.GetComponent<MetaDataComponent>(mech).EntityPrototype!.ID;
                Assert.That(actual, Is.InRange(targetSpeed * 0.95f, MathF.Min(targetSpeed, shuttleLimit) + 0.001f), id);
            }
            entities.DeleteEntity(space);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(1, false, 35f, 35f)]
    [TestCase(6, false, 85f, 35f)]
    [TestCase(4, true, 75f, 45f)]
    [TestCase(8, true, 175f, 45f)]
    public async Task InstalledPartsAndEngineRemovalUpdateDriftLimit(int tier, bool expandHull, float speedLimit, float fallbackSpeed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            if (expandHull)
            {
                var maps = entities.System<SharedMapSystem>();
                var tiles = new List<(Vector2i, Tile)>();
                for (var x = 0; x < 12; x++)
                for (var y = 0; y < 10; y++)
                    tiles.Add((new Vector2i(x, y), map.Tile.Tile));
                maps.SetTiles(map.Grid, tiles);
            }

            var transforms = entities.System<SharedTransformSystem>();
            var thrusters = entities.System<ThrusterSystem>();
            var physics = entities.System<SharedPhysicsSystem>();
            var uid = entities.SpawnEntity("DLFlightLimitThrusterProbe", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            transforms.AnchorEntity(uid);
            var thruster = entities.GetComponent<ThrusterComponent>(uid);
            var shuttle = entities.GetComponent<ShuttleComponent>(map.Grid);
            var refresh = new RefreshPartsEvent { PartRatings = { ["Capacitor"] = tier } };
            entities.EventBus.RaiseLocalEvent(uid, refresh);
            Assert.That(thruster.IsOn, Is.True);
            var body = entities.GetComponent<PhysicsComponent>(map.Grid);
            Assert.That(ShuttleFlightLimits.GridMaxSpeed(shuttle, body.FixturesMass), Is.EqualTo(speedLimit).Within(0.001));
            foreach (var center in shuttle.CenterOfThrust)
                Assert.That(float.IsFinite(center.X) && float.IsFinite(center.Y), Is.True, "Empty thrust directions must have finite centers");

            if (expandHull)
            {
                var mover = entities.System<MoverController>();
                var xform = entities.GetComponent<TransformComponent>(map.Grid);
                var initialAcceleration = 100f * MathF.Pow(1.25f, 3f) * 1.5f / body.Mass;
                Assert.That(mover.GetDirectionAccel(-Vector2.UnitY, shuttle, body, xform).Length(),
                    Is.EqualTo(initialAcceleration).Within(0.001));
                physics.SetLinearVelocity(map.Grid, -Vector2.UnitY * 55f, body: body);
                Assert.That(mover.GetDirectionAccel(-Vector2.UnitY, shuttle, body, xform).Length(),
                    Is.EqualTo(initialAcceleration * 0.8f).Within(0.001));
                physics.SetLinearVelocity(map.Grid, -Vector2.UnitY * 65f, body: body);
                Assert.That(mover.GetDirectionAccel(-Vector2.UnitY, shuttle, body, xform).Length(),
                    Is.EqualTo(initialAcceleration * 0.6f).Within(0.001));
            }

            // Cooldown is normal flight and must not be a way to evade the limit.
            entities.EnsureComponent<FTLComponent>(map.Grid).State = FTLState.Cooldown;
            physics.SetLinearVelocity(map.Grid, new Vector2(200, 200), body: body);
            physics.Update(1f / 30);
            Assert.That(body.LinearVelocity.Length(), Is.InRange(speedLimit - 0.5f, speedLimit + 0.001f));
            thrusters.DisableThruster(uid, thruster);
            physics.SetLinearVelocity(map.Grid, new Vector2(100, 100), body: body);
            physics.Update(1f / 30);
            Assert.That(body.LinearVelocity.Length(), Is.InRange(fallbackSpeed - 0.5f, fallbackSpeed + 0.001f));
            Assert.That(shuttle.RatedBaseLinearThrust, Is.All.EqualTo(0).Within(0.01));
            Assert.That(shuttle.AccelerationLinearThrust, Is.All.EqualTo(0).Within(0.01));
            Assert.That(shuttle.AccelerationRatedBaseLinearThrust, Is.All.EqualTo(0).Within(0.01));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(100f)]
    [TestCase(400f)]
    public async Task HighSpeedImpactStartsAtHullRatherThanInternalEquipment(float speed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var physics = entities.System<SharedPhysicsSystem>();
            for (var x = 0; x < 12; x++)
            for (var y = 0; y < 6; y++)
                maps.SetTile(map.Grid, new Vector2i(x, y), new Tile(1));
            entities.System<ShuttleSystem>().Disable(map.Grid);
            // Grid manifolds can contact either end of the debris's edge. Model a
            // continuous hull so each contact tile has exterior plating above it.
            var hullWalls = new List<EntityUid>();
            for (var y = 0; y < 6; y++)
                hullWalls.Add(entities.SpawnEntity("WallShuttle", new EntityCoordinates(map.Grid, 0.5f, y + 0.5f)));
            var internalWall = entities.SpawnEntity("WallShuttle", new EntityCoordinates(map.Grid, 5.5f, 2.5f));
            // Observe impact routing independently of the hull's flat armor reduction.
            // Remove it on both probes so an interior hit cannot be hidden by resistance.
            var damage = entities.System<DamageableSystem>();
            foreach (var wall in hullWalls)
                damage.SetDamageModifierSetId(wall, null);
            damage.SetDamageModifierSetId(internalWall, null);
            var debris = maps.CreateGridEntity(map.MapId);
            maps.SetTile(debris, Vector2i.Zero, new Tile(1));
            transforms.SetLocalPosition(debris, new Vector2(-2, 2));
            // Exercise collision handling above the normal flight envelope.
            entities.GetComponent<ShuttleComponent>(debris).Enabled = false;
            var body = entities.GetComponent<PhysicsComponent>(debris);
            physics.SetLinearVelocity(debris, new Vector2(speed, 0), body: body);
            physics.Update(1f / 30);
            physics.Update(1f / 30);
            var hullDamaged = false;
            foreach (var wall in hullWalls)
                hullDamaged |= !entities.EntityExists(wall) || entities.GetComponent<DamageableComponent>(wall).TotalDamage > 0;
            Assert.That(hullDamaged, Is.True, "At least one part of the continuous outer hull must receive the first impact");
            Assert.That(entities.EntityExists(internalWall), Is.True);
            Assert.That(entities.GetComponent<DamageableComponent>(internalWall).TotalDamage > 0, Is.False, "The first impact must not originate behind intact outer plating");
        });
        await pair.CleanReturnAsync();
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task EyeAndObjectsFollowTheSameRenderedGridRotation(bool hasMover)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<TransformSystem>();
            var map = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var observer = entities.SpawnEntity(null, new EntityCoordinates(grid, 2, 2));
            var item = entities.SpawnEntity(null, new EntityCoordinates(grid, 6, 2));
            var eye = entities.AddComponent<EyeComponent>(observer);
            var offset = Angle.Zero;
            if (hasMover)
            {
                var mover = entities.AddComponent<InputMoverComponent>(observer);
                mover.RelativeEntity = grid;
                offset = Angle.FromDegrees(15);
                mover.RelativeRotation = mover.TargetRelativeRotation = offset;
            }
            var eyes = entities.System<EyeLerpingSystem>();
            eyes.AddEye(observer, eye);
            transforms.ResetRenderTransforms();
            transforms.SetLocalRotation(grid, Angle.FromDegrees(90));
            var renderedAngle = transforms.GetRenderWorldRotation(grid.Owner);
            Assert.That(Math.Abs(Angle.ShortestDistance(renderedAngle, transforms.GetWorldRotation(grid.Owner)).Theta), Is.GreaterThan(0.01), "Fixture must exercise interpolation between simulation ticks");
            eyes.FrameUpdate(0);
            var expected = -(renderedAngle + offset);
            Assert.That(Math.Abs(Angle.ShortestDistance(eye.Rotation, expected).Theta), Is.LessThan(0.0001));
            var cameraRotation = eye.Rotation;
            var visualDelta = cameraRotation.RotateVec(transforms.GetRenderWorldPosition(item) - transforms.GetRenderWorldPosition(observer));
            var expectedDelta = (-offset).RotateVec(new Vector2(4, 0));
            Assert.That(Vector2.Distance(visualDelta, expectedDelta), Is.LessThan(0.0001), "Objects on the same rotating grid must keep their screen-relative direction");
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
