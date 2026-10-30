using System.Numerics;
using System.Collections.Generic;
using Content.Client.Eye;
using Content.Server.Construction;
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
    baseThrust: 100000
    thrust: 100000
    requireSpace: false
    burnShape: []
";

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(6)]
    public async Task InstalledPartsAndEngineRemovalUpdateDriftLimit(int tier)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
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
            Assert.That(ShuttleFlightLimits.GridMaxSpeed(shuttle), Is.EqualTo(35 + 10 * (tier - 1)).Within(0.001));
            foreach (var center in shuttle.CenterOfThrust)
                Assert.That(float.IsFinite(center.X) && float.IsFinite(center.Y), Is.True, "Empty thrust directions must have finite centers");
            var body = entities.GetComponent<PhysicsComponent>(map.Grid);
            // Cooldown is normal flight and must not be a way to evade the limit.
            entities.EnsureComponent<FTLComponent>(map.Grid).State = FTLState.Cooldown;
            physics.SetLinearVelocity(map.Grid, new Vector2(200, 200), body: body);
            physics.Update(1f / 30);
            Assert.That(body.LinearVelocity.Length(), Is.LessThanOrEqualTo(ShuttleFlightLimits.GridMaxSpeed(shuttle) + 0.001));
            thrusters.DisableThruster(uid, thruster);
            physics.SetLinearVelocity(map.Grid, new Vector2(100, 100), body: body);
            physics.Update(1f / 30);
            Assert.That(body.LinearVelocity.Length(), Is.LessThanOrEqualTo(35.001f));
            Assert.That(shuttle.RatedBaseLinearThrust, Is.All.EqualTo(0).Within(0.01));
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
