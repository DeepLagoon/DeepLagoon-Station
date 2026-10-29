using System.Linq;
using System.Numerics;
using Content.Shared.Interaction;
using Content.Shared.Wall;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class WallMountedInterfaceTests
{
    private enum TestKey { Key }

    [TestCase("APCBasic", false)]
    [TestCase("StationMap", false)]
    [TestCase("APCBasic", true)]
    [TestCase("StationMap", true)]
    public async Task OpenMountedInterfaceSurvivesUpdatesUntilActorLeaves(string prototype, bool connected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = connected, Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid actor = default, device = default;
        Enum key = default!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(map.Grid.Owner, map.Grid.Comp, Enumerable.Range(-2, 5)
                .SelectMany(x => Enumerable.Range(-2, 5).Select(y => (new Vector2i(x, y), floor))).ToList());
            var center = new Vector2(.5f, .5f);
            entities.SpawnEntity("WallSolid", new EntityCoordinates(map.Grid.Owner, center));
            device = entities.SpawnEntity(prototype, new EntityCoordinates(map.Grid.Owner, center));
            actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, center + Angle.Zero.ToWorldVec()));
            key = entities.GetComponent<Content.Shared.UserInterface.ActivatableUIComponent>(device).Key!;
            if (prototype == "StationMap")
                entities.System<Content.Server.Power.EntitySystems.PowerReceiverSystem>().SetNeedsPower(device, false);
            if (connected)
                pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var ui = entities.System<SharedUserInterfaceSystem>();
            Assert.That(entities.System<SharedInteractionSystem>().InRangeAndAccessible(actor, device), Is.True);
            ui.OpenUi(device, key, actor);
            Assert.That(ui.IsUiOpen(device, key, actor), Is.True);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.System<SharedUserInterfaceSystem>().IsUiOpen(device, key, actor), Is.True,
                "An open wall UI must survive the engine's recurring range checks for a living actor.");
            entities.System<SharedTransformSystem>().SetCoordinates(actor,
                new EntityCoordinates(map.Grid.Owner, new Vector2(5.5f, .5f)));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() => Assert.That(
            pair.Server.EntMan.System<SharedUserInterfaceSystem>().IsUiOpen(device, key, actor), Is.False,
            "The same interface must close when the actor actually leaves interaction range."));
        if (connected)
            await pair.Server.WaitAssertion(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MountedInterfacesStayOpenFromTheirFrontOnRotatedGrids()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        EntityUid map = default, grid = default, actor = default;
        var center = new Vector2(.5f, .5f);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId);
            var created = maps.CreateGridEntity(mapId); grid = created.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid, created.Comp, Enumerable.Range(-3, 7).SelectMany(x => Enumerable.Range(-3, 7).Select(y => (new Vector2i(x, y), floor))).ToList());
            entities.SpawnEntity("WallSolid", new EntityCoordinates(grid, center));
            actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid, center + Vector2.UnitY));
        });
        foreach (var prototype in new[] { "APCBasic", "Intercom", "AirAlarm", "ComputerWallmountRadar", "ComputerWallmountStationRecords" })
        {
            EntityUid device = default;
            await pair.Server.WaitAssertion(() => device = pair.Server.EntMan.SpawnEntity(prototype, new EntityCoordinates(grid, center)));
            foreach (var gridAngle in new[] { 0, 45, 90, 180 })
            foreach (var deviceAngle in new[] { 0, 90, 180, 270 })
            {
                var localRotation = Angle.FromDegrees(deviceAngle);
                await pair.Server.WaitAssertion(() =>
                {
                    var transforms = pair.Server.EntMan.System<SharedTransformSystem>();
                    transforms.SetLocalRotation(grid, Angle.FromDegrees(gridAngle));
                    transforms.SetLocalRotation(device, localRotation);
                    transforms.SetCoordinates(actor, new EntityCoordinates(grid, center + localRotation.ToWorldVec()));
                });
                await pair.RunTicksSync(1);
                await pair.Server.WaitAssertion(() =>
                {
                    var entities = pair.Server.EntMan;
                    var interaction = entities.System<SharedInteractionSystem>();
                    Assert.That(interaction.InRangeUnobstructed(actor, device), Is.True, $"Front interaction: {prototype}, grid={gridAngle}, device={deviceAngle}");
                    var ev = new BoundUserInterfaceCheckRangeEvent((device, entities.GetComponent<TransformComponent>(device)), TestKey.Key,
                        new InterfaceData("ApcBoundUserInterface"), (actor, entities.GetComponent<TransformComponent>(actor)));
                    entities.EventBus.RaiseLocalEvent(device, ref ev, true);
                    Assert.That(ev.Result, Is.EqualTo(BoundUserInterfaceRangeResult.Pass), $"UI range: {prototype}, grid={gridAngle}, device={deviceAngle}");
                });
                await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(actor, new EntityCoordinates(grid, center - localRotation.ToWorldVec())));
                await pair.RunTicksSync(1);
                await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.System<SharedInteractionSystem>().InRangeUnobstructed(actor, device), Is.False,
                    "The supporting wall must still block access from the back."));
                await pair.Server.WaitAssertion(() => pair.Server.EntMan.System<SharedTransformSystem>().SetCoordinates(actor, new EntityCoordinates(grid, center + localRotation.ToWorldVec() * 5)));
                await pair.RunTicksSync(1);
                await pair.Server.WaitAssertion(() =>
                {
                    var entities = pair.Server.EntMan;
                    var ev = new BoundUserInterfaceCheckRangeEvent((device, entities.GetComponent<TransformComponent>(device)), TestKey.Key,
                        new InterfaceData("ApcBoundUserInterface"), (actor, entities.GetComponent<TransformComponent>(actor)));
                    entities.EventBus.RaiseLocalEvent(device, ref ev, true);
                    Assert.That(ev.Result, Is.EqualTo(BoundUserInterfaceRangeResult.Fail), "Walking away must close the UI.");
                });
            }
            await pair.Server.WaitAssertion(() => pair.Server.EntMan.DeleteEntity(device));
        }
        await pair.Server.WaitAssertion(() => pair.Server.EntMan.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientWallUiRangeWaitsForServerObstructionCheck()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Destructive = true });
        NetEntity netActor = default, netDevice = default;
        EntityUid map = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTiles(grid.Owner, grid.Comp, Enumerable.Range(-2, 5).SelectMany(x => Enumerable.Range(-2, 5).Select(y => (new Vector2i(x, y), floor))).ToList());
            var center = new Vector2(.5f, .5f);
            entities.SpawnEntity("WallSolid", new EntityCoordinates(grid.Owner, center));
            var actor = entities.SpawnEntity("MobHuman", new EntityCoordinates(grid.Owner, center - Vector2.UnitY));
            netActor = entities.GetNetEntity(actor);
            var device = entities.SpawnEntity("APCBasic", new EntityCoordinates(grid.Owner, center));
            netDevice = entities.GetNetEntity(device);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), actor);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var actor = entities.GetEntity(netActor); var device = entities.GetEntity(netDevice);
            // Simulate a stale orientation while later snapshots of the mounted device are in flight.
            entities.GetComponent<WallMountComponent>(device).Direction = Angle.FromDegrees(180);
            Assert.That(entities.System<SharedInteractionSystem>().InRangeAndAccessible(actor, device), Is.False);
            BoundUserInterfaceCheckRangeEvent Check() => new((device, entities.GetComponent<TransformComponent>(device)), TestKey.Key,
                new InterfaceData("ApcBoundUserInterface"), (actor, entities.GetComponent<TransformComponent>(actor)));
            var ev = Check(); entities.EventBus.RaiseLocalEvent(device, ref ev, true);
            Assert.That(ev.Result, Is.EqualTo(BoundUserInterfaceRangeResult.Pass), "A client obstruction check must not make an authorized wall UI blink.");
            entities.System<SharedTransformSystem>().SetLocalPosition(actor, new Vector2(8, 8));
            ev = Check(); entities.EventBus.RaiseLocalEvent(device, ref ev, true);
            Assert.That(ev.Result, Is.EqualTo(BoundUserInterfaceRangeResult.Fail), "Distance checks remain predictive.");
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            pair.Server.EntMan.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

}
