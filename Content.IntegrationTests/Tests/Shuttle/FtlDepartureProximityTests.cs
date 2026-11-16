using System.Numerics;
using System.Reflection;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Ships;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.IntegrationTests.Tests.Shuttle;

[TestFixture]
public sealed class FtlDepartureProximityTests
{
    [TestCase(50f, 768f)]
    [TestCase(150f, 1536f)]
    public async Task ConsoleAllowsDepartureWhenOtherShuttleMovesOutOfRange(float mass, float range)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var map = maps.CreateMap(out var mapId);
            var ship = maps.CreateGridEntity(mapId);
            var other = maps.CreateGridEntity(mapId);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(ship, Vector2i.Zero, tile);
            maps.SetTile(other, Vector2i.Zero, tile);

            var physics = entities.System<SharedPhysicsSystem>();
            var fixtures = entities.GetComponent<FixturesComponent>(other);
            var body = entities.GetComponent<PhysicsComponent>(other);
            var densityMultiplier = mass / body.Mass;
            foreach (var (id, fixture) in fixtures.Fixtures)
                physics.SetDensity(other, id, fixture, fixture.Density * densityMultiplier, manager: fixtures);
            Assert.That(body.Mass, Is.EqualTo(mass).Within(0.001f));

            var origin = new Vector2(2000f, 3000f);
            transforms.SetWorldPosition(ship, origin);
            var consoleOffset = new Vector2(0.5f);
            var console = entities.SpawnEntity(null, new EntityCoordinates(ship, consoleOffset));
            var consoleComp = entities.AddComponent<ShuttleConsoleComponent>(console);
            var otherConsole = entities.SpawnEntity(null, new EntityCoordinates(other, consoleOffset));
            entities.AddComponent<ShuttleConsoleComponent>(otherConsole);
            entities.AddComponent<FTLDriveComponent>(console);

            var consoles = entities.System<ShuttleConsoleSystem>();
            var depart = typeof(ShuttleConsoleSystem).GetMethod("ConsoleFTL", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var target = new EntityCoordinates(map, origin - new Vector2(256f, 0f));

            void RequestJump()
            {
                depart.Invoke(consoles, new object[] { new Entity<ShuttleConsoleComponent>(console, consoleComp), target, Angle.Zero, mapId });
            }

            transforms.SetWorldPosition(other, origin + new Vector2(range - 2f, 0f));
            RequestJump();
            Assert.That(entities.HasComponent<FTLComponent>(ship), Is.False);

            transforms.SetWorldPosition(other, origin + new Vector2(range + 2f, 0f));
            RequestJump();
            Assert.That(entities.TryGetComponent<FTLComponent>(ship, out var ftl), Is.True,
                "Departure should start once the other shuttle is out of range.");
            Assert.That(ftl.State, Is.EqualTo(FTLState.Starting));
        });
        await pair.CleanReturnAsync();
    }
}
