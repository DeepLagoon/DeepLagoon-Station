using System.Numerics;
using System.Reflection;
using Content.IntegrationTests.Pair;
using Content.Client.Interaction;
using Content.Client._Emberfall.Weapons.Ranged.Systems;
using Content.Client._Goobstation.Weapons.LaserPointer;
using Content.Shared._Emberfall.Weapons.Ranged;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Moq;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

[TestFixture, NonParallelizable]
public sealed class MovingGridEffectsTests
{
    private TestPair _pair = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_pair != null)
            await _pair.DisposeAsync();
    }

    [TestCase(0f)]
    [TestCase(0.3f)]
    public async Task DragShadowStaysAtTheCursorAcrossGridInterpolation(float rotation)
    {
        var pair = _pair;
        await pair.Client.WaitAssertion(() =>
        {
            var em = pair.Client.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transforms = em.System<TransformSystem>();
            var map = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            for (var x = 0; x < 8; x++)
            for (var y = 0; y < 8; y++)
                maps.SetTile(grid, new Vector2i(x, y), new Tile(1));
            var shadow = em.SpawnEntity("dragshadow", new EntityCoordinates(map, 2, 2));
            transforms.SetParent(shadow, map);
            var drag = em.System<DragDropSystem>();
            var eyeField = typeof(DragDropSystem).GetField("_eyeManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shadowField = typeof(DragDropSystem).GetField("_dragShadow", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var oldEye = eyeField.GetValue(drag);
            var oldShadow = shadowField.GetValue(drag);
            var cursorPosition = new MapCoordinates(new Vector2(2, 2), mapId);
            var eye = new Mock<IEyeManager>();
            eye.Setup(value => value.PixelToMap(It.IsAny<ScreenCoordinates>())).Returns(() => cursorPosition);
            eyeField.SetValue(drag, eye.Object);
            shadowField.SetValue(drag, shadow);
            try
            {
                for (var tick = 1; tick <= 5; tick++)
                {
                    transforms.ResetRenderTransforms();
                    transforms.SetLocalPositionRotation(grid, new Vector2(tick * 0.1f, 0), new Angle(rotation * tick));
                    var cursor = transforms.GetRenderWorldPosition(grid.Owner) + new Vector2(2f + tick * 0.02f, 2f);
                    cursorPosition = new MapCoordinates(cursor, mapId);
                    drag.FrameUpdate(1f / 60);
                    Assert.That(em.GetComponent<TransformComponent>(shadow).ParentUid, Is.EqualTo(map),
                        "A cursor preview must not be picked up by grid traversal");
                    Assert.That(Vector2.Distance(transforms.GetRenderWorldPosition(shadow), cursor), Is.LessThan(0.0001f));
                }
                var nextMap = maps.CreateMap(out var nextId);
                var nextCursor = new Vector2(20, 15);
                cursorPosition = new MapCoordinates(nextCursor, nextId);
                drag.FrameUpdate(1f / 60);
                Assert.That(em.GetComponent<TransformComponent>(shadow).ParentUid, Is.EqualTo(nextMap));
                Assert.That(Vector2.Distance(transforms.GetRenderWorldPosition(shadow), nextCursor), Is.LessThan(0.0001f));
            }
            finally
            {
                eyeField.SetValue(drag, oldEye);
                shadowField.SetValue(drag, oldShadow);
            }
        });
    }

    [TestCase(0f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 0.3f)]
    [TestCase(1f, -0.3f)]
    public async Task LaserInputAndBothRenderedEndpointsUseTheSameGridFrame(float travel, float rotation)
    {
        var pair = _pair;
        await pair.Client.WaitAssertion(() =>
        {
            var em = pair.Client.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transforms = em.System<TransformSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, new Vector2i(3, 2), new Tile(1));
            var holder = em.SpawnEntity(null, new EntityCoordinates(grid, 3, 2));
            em.GetComponent<TransformComponent>(holder).GridTraversal = false;
            Assert.That(em.GetComponent<TransformComponent>(holder).GridUid, Is.EqualTo(grid.Owner));
            transforms.ResetRenderTransforms();
            transforms.SetLocalPositionRotation(grid, new Vector2(travel, travel / 2f), new Angle(rotation));
            Entity<TransformComponent> source = (holder, em.GetComponent<TransformComponent>(holder));
            var renderedStart = transforms.GetRenderWorldPosition(source);
            var targetOnDeck = new Vector2(7, 5);
            var cursor = Vector2.Transform(targetOnDeck, transforms.GetRenderWorldMatrix(grid.Owner));
            var simulatedDirection = LaserPointerRenderCoordinates.ToSimulationDirection(transforms, source, cursor);
            var simulationTarget = Vector2.Transform(targetOnDeck, transforms.GetWorldMatrix(grid.Owner));
            Assert.That(Vector2.Distance(simulatedDirection, simulationTarget - transforms.GetWorldPosition(holder)),
                Is.LessThan(0.0001f), "Raycasts must use the corresponding simulation-space target");
            var line = LaserPointerRenderCoordinates.GetLine(transforms, source, simulatedDirection);
            Assert.That(Vector2.Distance(line.Start, renderedStart), Is.LessThan(0.0001f));
            Assert.That(Vector2.Distance(line.End, cursor), Is.LessThan(0.0001f),
                "Moving or rotating the grid must not change the displayed aim point");
            Assert.That(line.End - line.Start, Is.Not.EqualTo(Vector2.Zero));
        });
    }

    [TestCase(0f)]
    [TestCase(0.2f)]
    public async Task TracerSamplesDisplayedFramesRatherThanPredictionTicks(float rotation)
    {
        var pair = _pair;
        await pair.Client.WaitAssertion(() =>
        {
            var em = pair.Client.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transforms = em.System<TransformSystem>();
            var tracers = em.System<TracerSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            maps.SetTile(grid, new Vector2i(1, 1), new Tile(1));
            var projectile = em.SpawnEntity(null, new EntityCoordinates(grid, 1, 1));
            em.GetComponent<TransformComponent>(projectile).GridTraversal = false;
            Assert.That(em.GetComponent<TransformComponent>(projectile).GridUid, Is.EqualTo(grid.Owner));
            var tracer = em.AddComponent<TracerComponent>(projectile);
            tracer.Length = 100;
            for (var tick = 1; tick <= 4; tick++)
            {
                transforms.ResetRenderTransforms();
                transforms.SetLocalPositionRotation(grid, new Vector2(tick, 0), new Angle(rotation * tick));
                transforms.SetLocalPosition(projectile, new Vector2(1 + tick, 1));
                var count = tracer.Data.PositionHistory.Count;
                tracers.Update(1f / 30);
                tracers.Update(1f / 30);
                Assert.That(tracer.Data.PositionHistory.Count, Is.EqualTo(count),
                    "Replayed prediction ticks must not insert backwards segments into the trail");
                tracers.FrameUpdate(1f / 60);
                Assert.That(tracer.Data.PositionHistory.Count, Is.EqualTo(count + 1));
                Assert.That(Vector2.Distance(tracer.Data.PositionHistory[^1], transforms.GetRenderWorldPosition(projectile)),
                    Is.LessThan(0.0001f));
            }
            var nextMap = maps.CreateMap(out _);
            transforms.SetParent(projectile, nextMap);
            tracers.FrameUpdate(1f / 60);
            Assert.That(tracer.Data.PositionHistory.Count, Is.EqualTo(1), "A map change must not leave a cross-map trail");
        });
    }
}
