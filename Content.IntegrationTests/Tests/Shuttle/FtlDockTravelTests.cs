using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
public sealed class FtlDockTravelTests
{
    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(true, false)]
    public async Task DestinationStaysBehindUntilShuttleArrives(bool separateMaps, bool freeDock)
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
            var docking = entities.System<DockingSystem>();
            entities.DeleteEntity(origin.Grid);
            entities.DeleteEntity(destination.Grid);
            var shuttle = maps.CreateGridEntity(origin.MapId);
            var station = maps.CreateGridEntity(separateMaps ? destination.MapId : origin.MapId);
            var stationMap = transforms.GetMapId(station.Owner);
            var stationPosition = new Vector2(50, 50);
            var stationRotation = Angle.FromDegrees(37);
            transforms.SetLocalPosition(station.Owner, stationPosition);
            transforms.SetLocalRotation(station.Owner, stationRotation);

            var tiles = new List<(Vector2i Index, Tile Tile)>
            {
                (new Vector2i(0, 0), new Tile(1)),
                (new Vector2i(0, 1), new Tile(1)),
                (new Vector2i(0, 2), new Tile(1)),
            };
            maps.SetTiles(shuttle.Owner, shuttle.Comp, tiles);
            maps.SetTiles(station.Owner, station.Comp, tiles);
            var shuttleComp = entities.EnsureComponent<ShuttleComponent>(shuttle.Owner);
            entities.EnsureComponent<ShuttleComponent>(station.Owner);
            Assert.That(entities.EnsureComponent<FTLLockComponent>(station.Owner).Enabled, Is.True);
            entities.EnsureComponent<FTLDestinationComponent>(entities.GetComponent<TransformComponent>(station.Owner).MapUid!.Value);
            var shuttlePort = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(shuttle.Owner, new Vector2(0.5f)));
            var stationPort = freeDock
                ? entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(station.Owner, new Vector2(0.5f)))
                : EntityUid.Invalid;
            Assert.That(docking.GetDockingConfig(shuttle.Owner, station.Owner) != null, Is.EqualTo(freeDock));
            var originalCoordinates = entities.GetComponent<TransformComponent>(shuttle.Owner).Coordinates;
            var originalRotation = transforms.GetWorldRotation(shuttle.Owner);

            void AssertStationStayedBehind()
            {
                Assert.That(transforms.GetMapId(station.Owner), Is.EqualTo(stationMap));
                Assert.That(transforms.GetWorldPosition(station.Owner), Is.EqualTo(stationPosition));
                Assert.That(transforms.GetWorldRotation(station.Owner).EqualsApprox(stationRotation), Is.True);
                Assert.That(entities.HasComponent<FTLComponent>(station.Owner), Is.False);
            }

            shuttles.FTLToDock(shuttle.Owner, shuttleComp, station.Owner, startupTime: 10, hyperspaceTime: 30);
            var ftl = entities.GetComponent<FTLComponent>(shuttle.Owner);
            Assert.That(ftl.State, Is.EqualTo(FTLState.Starting));
            Assert.That(entities.GetComponent<TransformComponent>(shuttle.Owner).Coordinates, Is.EqualTo(originalCoordinates),
                "Planning a docked jump must not teleport the shuttle to its destination");
            Assert.That(transforms.GetWorldRotation(shuttle.Owner), Is.EqualTo(originalRotation));
            Assert.That(entities.GetComponent<DockingComponent>(shuttlePort).Docked, Is.False);
            AssertStationStayedBehind();

            // Advance the real transition handlers without waiting for wall-clock FTL timers.
            Entity<FTLComponent, ShuttleComponent> flight = (shuttle.Owner, ftl, shuttleComp);
            typeof(ShuttleSystem).GetMethod("UpdateFTLStarting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shuttles, new object[] { flight });
            Assert.That(ftl.State, Is.EqualTo(FTLState.Travelling));
            Assert.That(entities.HasComponent<FTLMapComponent>(entities.GetComponent<TransformComponent>(shuttle.Owner).MapUid), Is.True);
            AssertStationStayedBehind();
            var group = new HashSet<EntityUid>();
            shuttles.GetAllDockedShuttles(shuttle.Owner, group);
            Assert.That(group, Is.EquivalentTo(new[] { shuttle.Owner }));

            typeof(ShuttleSystem).GetMethod("UpdateFTLArriving", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shuttles, new object[] { flight });
            Assert.That(transforms.GetMapId(shuttle.Owner), Is.EqualTo(stationMap));
            Assert.That(ftl.State, Is.EqualTo(FTLState.Cooldown));
            AssertStationStayedBehind();
            Assert.That(entities.GetComponent<DockingComponent>(shuttlePort).Docked, Is.EqualTo(freeDock));
            if (freeDock)
                Assert.That(entities.GetComponent<DockingComponent>(shuttlePort).DockedWith, Is.EqualTo(stationPort));
        });
        await pair.CleanReturnAsync();
    }
}
