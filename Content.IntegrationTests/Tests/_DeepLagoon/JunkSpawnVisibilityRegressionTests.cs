using System;
using System.Numerics;
using Content.Server._DeepLagoon.SpaceEnvironment;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared.Station.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class JunkSpawnVisibilityRegressionTests
{
    [TestCase("MobObserver", false)]
    [TestCase("MobObserver", true)]
    [TestCase("MobHuman", false)]
    public async Task AutomaticFieldJunkActuallyEntersTheObserversNormalView(string prototype, bool flying)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        EntityUid map = default, actor = default;
        var encountered = false;
        var created = false;
        NetEntity visibleWall = default;
        TimeSpan start = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition
            {
                Id = "view-regression", Seed = 713,
                Planets = new() { new SectorPlanet { X = 10000 } },
                Regions = new() { new SpaceRegion { Id = "wreckage", X = 5000, Radius = 2000, Kind = SpaceRegionKind.Debris } },
            };
            actor = entities.SpawnEntity(prototype, new EntityCoordinates(map, new Vector2(5000, 0)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, actor);
            if (flying)
                entities.System<SharedMoverController>().SetVelocityDirection(
                    (actor, entities.GetComponent<InputMoverComponent>(actor)), Direction.East, 0, true);
            start = pair.Server.ResolveDependency<IGameTiming>().CurTime;
        });
        for (var step = 0; step < 180 && !encountered; step++)
        {
            await pair.RunTicksSync(15);
            var elapsed = TimeSpan.Zero;
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var xforms = entities.System<SharedTransformSystem>();
                var position = xforms.GetWorldPosition(actor);
                var normalHalfView = pair.Server.ResolveDependency<IConfigurationManager>().GetCVar(Robust.Shared.CVars.NetMaxUpdateRange) * 0.5f;
                var query = entities.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
                while (query.MoveNext(out _, out _, out var transform))
                {
                    if (transform.MapUid != map) continue;
                    created = true;
                    var children = transform.ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        if (entities.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID
                            .StartsWith("WallRockScrapPile", StringComparison.Ordinal) != true) continue;
                        var wallPosition = xforms.GetWorldPosition(child);
                        if (MathF.Abs(wallPosition.X - position.X) > normalHalfView
                            || MathF.Abs(wallPosition.Y - position.Y) > normalHalfView) continue;
                        visibleWall = entities.GetNetEntity(child);
                        encountered = true;
                        break;
                    }
                    if (encountered) break;
                }
                elapsed = pair.Server.ResolveDependency<IGameTiming>().CurTime - start;
            });
            if (elapsed.TotalSeconds >= 45) break;
        }
        await pair.Server.WaitAssertion(() =>
        {
            if (flying) Assert.That(pair.Server.EntMan.System<SharedTransformSystem>().GetWorldPosition(actor).X,
                Is.GreaterThan(5005f), "This regression must exercise real ghost movement");
            Assert.That(created, Is.True, "The field must create physical junk automatically");
            Assert.That(encountered, Is.True, "A nonzero spawn counter is insufficient: a scrap wall must reach the ordinary eye envelope");
        });
        await pair.RunTicksSync(4);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var wall = entities.GetEntity(visibleWall);
            Assert.That(entities.EntityExists(wall), Is.True, "The encountered scrap wall must reach the connected client");
            Assert.That(entities.GetComponent<SpriteComponent>(wall).Visible, Is.True);
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task APlayerOwnedShipRegisteredAsAStationDoesNotSuppressItsOwnEncounters()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true, InLobby = true });
        EntityUid map = default, station = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId);
            entities.EnsureComponent<SpaceEnvironmentComponent>(map).Definition = new SectorDefinition
            {
                Id = "ship-regression", Seed = 51,
                Planets = new() { new SectorPlanet { X = 10000 } },
                Regions = new() { new SpaceRegion { X = 5000, Radius = 2000, Kind = SpaceRegionKind.Debris } },
            };
            var ship = maps.CreateGridEntity(mapId);
            maps.SetTile(ship, Vector2i.Zero, new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId));
            entities.System<SharedTransformSystem>().SetWorldPosition(ship, new Vector2(5000, 0));
            station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station, ship);
            entities.System<ShipOwnershipSystem>().RegisterShipOwnership(ship, pair.Player!);
            var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(ship, new Vector2(0.5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, actor);
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.System<OrbitalDebrisSystem>().ActiveCount, Is.GreaterThan(0));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            pair.Server.EntMan.DeleteEntity(station);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
