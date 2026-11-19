using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Server._DeepLagoon.Mapping;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
public sealed class FtlArrivalSafetyTests
{
    [TestCase(0, 0, false)]
    [TestCase(20, -20, false)]
    [TestCase(-20, 20, false)]
    [TestCase(0, 0, true)]
    public async Task ProximityKeepsTheEntireOffCenterHullClear(int offsetX, int offsetY, bool mappedBus)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var origin = await pair.CreateTestMap();
        var destination = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var shuttles = entities.System<ShuttleSystem>();
            var random = pair.Server.ResolveDependency<IRobustRandom>();
            entities.DeleteEntity(origin.Grid);
            entities.DeleteEntity(destination.Grid);
            var shuttle = maps.CreateGridEntity(origin.MapId);
            var station = maps.CreateGridEntity(destination.MapId);
            var hull = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 8; x++)
            for (var y = 0; y < 20; y++)
                hull.Add((new Vector2i(offsetX + x, offsetY + y), new Tile(1)));
            if (mappedBus)
            {
                entities.DeleteEntity(shuttle.Owner);
                Assert.That(entities.System<MapLoaderSystem>().TryLoadGridTracked(origin.MapId,
                    new ResPath("/SharedMaps/_Mono/Shuttles/Bus/busdart.yml"), out var loaded), Is.True);
                shuttle = loaded!.Value;
            }
            else
                maps.SetTiles(shuttle.Owner, shuttle.Comp, hull);
            var stationTiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 64; x++)
            for (var y = 0; y < 64; y++)
                stationTiles.Add((new Vector2i(x, y), new Tile(1)));
            maps.SetTiles(station.Owner, station.Comp, stationTiles);
            transforms.SetLocalPosition(station.Owner, new Vector2(50, 50));
            transforms.SetLocalRotation(station.Owner, Angle.FromDegrees(37));

            for (var seed = 0; seed < 128; seed++)
            {
                random.SetSeed(seed);
                Assert.That(shuttles.TryFTLProximity(shuttle.Owner, station.Owner), Is.True);
                var stationBounds = transforms.GetWorldMatrix(station.Owner).TransformBox(station.Comp.LocalAABB);
                var shuttleBounds = transforms.GetWorldMatrix(shuttle.Owner).TransformBox(shuttle.Comp.LocalAABB);
                Assert.That(shuttleBounds.Intersects(stationBounds), Is.False,
                    $"The rotated hull overlaps the station for seed {seed}, local origin ({offsetX}, {offsetY})");
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DockFlightRechecksTheDestinationOnArrival(bool dockBecomesFree)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var origin = await pair.CreateTestMap();
        var destination = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var shuttles = entities.System<ShuttleSystem>();
            entities.DeleteEntity(origin.Grid);
            entities.DeleteEntity(destination.Grid);
            var shuttle = maps.CreateGridEntity(origin.MapId);
            var station = maps.CreateGridEntity(destination.MapId);
            maps.SetTile(shuttle, Vector2i.Zero, new Tile(1));
            maps.SetTile(station, Vector2i.Zero, new Tile(1));
            entities.EnsureComponent<FTLDestinationComponent>(destination.MapUid).Enabled = true;
            var shuttleComponent = entities.EnsureComponent<ShuttleComponent>(shuttle.Owner);
            // A station is physically docked but is not part of the bus's FTL travel group.
            entities.System<ShuttleConsoleSystem>().SetFTLLock(station.Owner, false);
            var shuttlePort = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(shuttle.Owner, new Vector2(0.5f)));
            shuttles.FTLToDock(shuttle.Owner, shuttleComponent, station.Owner, startupTime: 10, hyperspaceTime: 30);
            var ftl = entities.GetComponent<FTLComponent>(shuttle.Owner);
            Entity<FTLComponent, ShuttleComponent> flight = (shuttle.Owner, ftl, shuttleComponent);
            typeof(ShuttleSystem).GetMethod("UpdateFTLStarting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shuttles, new object[] { flight });

            // The station moves over the old fallback point while the bus is in hyperspace.
            var planned = transforms.ToMapCoordinates(ftl.TargetCoordinates);
            transforms.SetLocalPosition(station.Owner, planned.Position - new Vector2(0.5f));
            if (dockBecomesFree)
            {
                entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(station.Owner, new Vector2(0.5f)));
                // A nearby ship must not cause the post-arrival system to move
                // the newly docked bus away from its station and docking joint.
                var nearby = maps.CreateGridEntity(destination.MapId);
                maps.SetTile(nearby, Vector2i.Zero, new Tile(1));
                transforms.SetLocalPosition(nearby.Owner, transforms.GetWorldPosition(station.Owner) + new Vector2(10, 10));
            }

            typeof(ShuttleSystem).GetMethod("UpdateFTLArriving", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shuttles, new object[] { flight });
            Assert.That(transforms.GetMapId(shuttle.Owner), Is.EqualTo(destination.MapId));
            var stationBounds = transforms.GetWorldMatrix(station.Owner).TransformBox(station.Comp.LocalAABB);
            var shuttleBounds = transforms.GetWorldMatrix(shuttle.Owner).TransformBox(shuttle.Comp.LocalAABB);
            Assert.That(shuttleBounds.Enlarged(-0.01f).Intersects(stationBounds), Is.False,
                "Arrival must not use the now occupied pre-flight fallback point");
            Assert.That(entities.GetComponent<DockingComponent>(shuttlePort).Docked, Is.EqualTo(dockBecomesFree),
                "Retry docking when a port becomes available during the trip");
            if (dockBecomesFree)
            {
                var dockedWith = entities.GetComponent<DockingComponent>(shuttlePort).DockedWith!.Value;
                Assert.That(Vector2.Distance(transforms.GetWorldPosition(shuttlePort), transforms.GetWorldPosition(dockedWith)),
                    Is.EqualTo(1f).Within(0.01f), "A nearby ship must not pull the bus away from its connected port");
            }
            Assert.That(maps.GetTileRef(station.Owner, station.Comp, Vector2i.Zero).Tile.IsEmpty, Is.False,
                "Arrival must not flatten the station's floor");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PostArrivalRepositionChecksRotatedHullAndItsLastResort()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var collision = entities.System<FTLAntiCollisionSystem>();
            var random = pair.Server.ResolveDependency<IRobustRandom>();
            entities.DeleteEntity(map.Grid);
            var shuttle = maps.CreateGridEntity(map.MapId);
            maps.SetTile(shuttle, new Vector2i(200, -150), new Tile(1));
            transforms.SetLocalRotation(shuttle.Owner, Angle.FromDegrees(90));
            var obstacle = maps.CreateGridEntity(map.MapId);
            maps.SetTile(obstacle, Vector2i.Zero, new Tile(1));
            var proposedPosition = new Vector2(100, 100);
            var hullCenter = transforms.GetWorldMatrix(shuttle.Owner).TransformBox(shuttle.Comp.LocalAABB).Center;
            transforms.SetLocalPosition(obstacle.Owner, proposedPosition + hullCenter - new Vector2(0.5f));
            var clear = typeof(FTLAntiCollisionSystem).GetMethod("IsPositionClear", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ignored = new HashSet<EntityUid> { shuttle.Owner };
            Assert.That(clear.Invoke(collision, new object[] { shuttle.Owner, map.MapId, proposedPosition, ignored }), Is.False,
                "The hull can hit the station even when the ship's origin is far away");

            // Block every sampled candidate, including the final, wider fallback.
            const int seed = 12345;
            random.SetSeed(seed);
            var positions = new List<Vector2>();
            for (var attempt = 0; attempt < 11; attempt++)
                positions.Add(random.NextAngle().ToVec() * (attempt < 10 ? 50f + attempt * 15f : 350f));
            foreach (var position in positions)
            {
                var blocker = maps.CreateGridEntity(map.MapId);
                maps.SetTile(blocker, Vector2i.Zero, new Tile(1));
                transforms.SetLocalPosition(blocker.Owner, position + hullCenter - new Vector2(0.5f));
            }
            random.SetSeed(seed);
            var find = typeof(FTLAntiCollisionSystem).GetMethod("FindSafePosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.That(find.Invoke(collision, new object[] { shuttle.Owner, map.MapId, Vector2.Zero, ignored }),
                Is.EqualTo(Vector2.Zero), "An exhausted search must not teleport into the unchecked last resort");
        });
        await pair.CleanReturnAsync();
    }
}
