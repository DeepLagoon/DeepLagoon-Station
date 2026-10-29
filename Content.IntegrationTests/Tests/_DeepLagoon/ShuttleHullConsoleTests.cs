using System.Linq;
using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Shuttles.Systems;
using Content.Shared.Maps;
using Content.Shared.Power.Components;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleHullConsoleTests
{
    [Test]
    public async Task NamedLossAndResetUseCurrentShipAsBaselineForEveryConsole()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 4; x++)
                maps.SetTile(grid, new Vector2i(x, 0), floor);
            var console = Spawn(entities, grid.Owner, "ComputerShuttle", 0);
            var otherConsole = Spawn(entities, grid.Owner, "ComputerShuttle", 1);
            var engine = Spawn(entities, grid.Owner, "Thruster", 2);
            var weapon = Spawn(entities, grid.Owner, "ShuttleGunKinetic", 3);
            var actor = Spawn(entities, grid.Owner, "MobHuman", 0);
            ReadHull(entities, console);
            Assert.That(entities.HasComponent<Content.Shared.Pinpointer.NavMapComponent>(grid.Owner), Is.True,
                "Non-station shuttles need the same nav map geometry as crew monitoring");
            var engineName = entities.GetComponent<MetaDataComponent>(engine).EntityName;
            entities.DeleteEntity(engine);
            var lost = ReadHull(entities, console);
            Assert.That(lost.SingleMissingName, Is.EqualTo(engineName));
            Assert.That(lost.MissingThrusters, Is.EqualTo(1));

            void Reset() => entities.EventBus.RaiseLocalEvent(console,
                new ShuttleHullResetMessage { Actor = actor, UiKey = ShuttleConsoleUiKey.Key });
            entities.GetComponent<ApcPowerReceiverComponent>(console).Powered = false;
            Reset();
            Assert.That(ReadHull(entities, console).MissingThrusters, Is.EqualTo(1), "Unpowered consoles cannot reset losses");
            entities.GetComponent<ApcPowerReceiverComponent>(console).Powered = true;
            // Test grids have no ownership lock; explicitly unlock the console itself as well.
            entities.RemoveComponent<ShuttleConsoleLockComponent>(console);
            entities.RemoveComponent<Content.Shared.Access.Components.AccessReaderComponent>(console);
            Reset();
            var accepted = ReadHull(entities, otherConsole);
            Assert.That(accepted.ExpectedThrusters, Is.Zero);
            Assert.That(accepted.MissingThrusters, Is.Zero);
            Assert.That(accepted.SingleMissingName, Is.Null);
            Assert.That(accepted.IntegrityFraction, Is.EqualTo(1f));
            Assert.That(accepted.Cells.All(cell => (cell & 2) == 0), Is.True);

            var weaponName = entities.GetComponent<MetaDataComponent>(weapon).EntityName;
            entities.DeleteEntity(weapon);
            var subsequent = ReadHull(entities, console);
            Assert.That(subsequent.MissingWeapons, Is.EqualTo(1));
            Assert.That(subsequent.MissingThrusters, Is.Zero);
            Assert.That(subsequent.SingleMissingName, Is.EqualTo(weaponName));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HullRecordsLossesBeforeFirstConsoleUseAndTracksRepairsAndConstruction()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid console = default, grid = default, addedEngine = default, addedWeapon = default, addedWall = default;
        int initialStructures = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var created = maps.CreateGridEntity(mapId);
            grid = created.Owner;
            Assert.That(entities.HasComponent<ShuttleComponent>(grid), Is.True);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 8; x++)
                maps.SetTile(created, new Vector2i(x, 0), floor);
            console = Spawn(entities, grid, "ComputerShuttle", 0);
            var engine = Spawn(entities, grid, "Thruster", 1);
            var weapon = Spawn(entities, grid, "ShuttleGunKinetic", 2);
            var wall = Spawn(entities, grid, "WallSolid", 3);
            Assert.That(entities.GetComponent<TransformComponent>(engine).Anchored, Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(weapon).Anchored, Is.True);
            Assert.That(entities.GetComponent<TransformComponent>(wall).Anchored, Is.True);

            // No console state has been requested and no periodic scan has run yet.
            entities.DeleteEntity(engine);
            entities.DeleteEntity(weapon);
            entities.DeleteEntity(wall);
            var state = ReadHull(entities, console);
            initialStructures = state.ExpectedStructures;
            Assert.That(state.ExpectedThrusters, Is.EqualTo(1));
            Assert.That(state.MissingThrusters, Is.EqualTo(1));
            Assert.That(state.ExpectedWeapons, Is.EqualTo(1));
            Assert.That(state.MissingWeapons, Is.EqualTo(1));
            Assert.That(state.MissingStructures, Is.EqualTo(1));
            Assert.That(state.Cells.Count(cell => (cell & 2) != 0), Is.EqualTo(3),
                "Removed objects must retain red schematic cells before anyone opens the console");

            Spawn(entities, grid, "Thruster", 1);
            Spawn(entities, grid, "ShuttleGunKinetic", 2);
            Spawn(entities, grid, "WallSolid", 3);
            addedEngine = Spawn(entities, grid, "Thruster", 4);
            addedWeapon = Spawn(entities, grid, "ShuttleGunKinetic", 5);
            addedWall = Spawn(entities, grid, "WallSolid", 6);
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var state = ReadHull(entities, console);
            Assert.That(state.ExpectedThrusters, Is.EqualTo(2));
            Assert.That(state.ExpectedWeapons, Is.EqualTo(2));
            Assert.That(state.ExpectedStructures, Is.EqualTo(initialStructures + 1));
            Assert.That(state.MissingThrusters + state.MissingWeapons + state.MissingStructures, Is.Zero,
                "Same-category replacements repair slots without duplicating their baseline");
            Assert.That(state.Cells.All(cell => (cell & 2) == 0), Is.True);
            entities.DeleteEntity(addedEngine);
            entities.DeleteEntity(addedWeapon);
            entities.DeleteEntity(addedWall);
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var state = ReadHull(pair.Server.EntMan, console);
            Assert.That(state.ExpectedThrusters, Is.EqualTo(2));
            Assert.That(state.ExpectedWeapons, Is.EqualTo(2));
            Assert.That(state.MissingThrusters, Is.EqualTo(1));
            Assert.That(state.MissingWeapons, Is.EqualTo(1));
            Assert.That(state.MissingStructures, Is.EqualTo(1));
            Assert.That(state.Cells.Count(cell => (cell & 2) != 0), Is.EqualTo(3),
                "Player-built engines, guns and walls must join subsequent damage accounting");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HullTelemetrySeparatesWorkingPropulsionAndCountsOnlyGridPowerStorage()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid console = default, engine = default, gyro = default;
        EntityUid[] storage = [];
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 10; x++)
                maps.SetTile(grid, new Vector2i(x, 0), floor);
            console = Spawn(entities, grid.Owner, "ComputerShuttle", 0);
            engine = Spawn(entities, grid.Owner, "Thruster", 1);
            gyro = Spawn(entities, grid.Owner, "Gyroscope", 2);
            var offlineEngine = Spawn(entities, grid.Owner, "Thruster", 3);
            var offlineGyro = Spawn(entities, grid.Owner, "Gyroscope", 4);
            var power = entities.System<PowerReceiverSystem>();
            power.SetNeedsPower(engine, false);
            power.SetNeedsPower(gyro, false);
            power.SetPowerDisabled(offlineEngine, true);
            power.SetPowerDisabled(offlineGyro, true);
            storage = [
                Spawn(entities, grid.Owner, "SMESBasic", 5),
                Spawn(entities, grid.Owner, "SubstationBasic", 6),
                Spawn(entities, grid.Owner, "APCBasic", 7),
            ];
            var cell = Spawn(entities, grid.Owner, "PowerCellSmall", 8);
            Assert.That(entities.GetComponent<BatteryComponent>(cell).CurrentCharge, Is.GreaterThan(0));
            Assert.That(storage.All(uid => entities.HasComponent<PowerNetworkBatteryComponent>(uid)), Is.True);

            // A battery on another ship must not increase this console's stored-energy total.
            var otherGrid = maps.CreateGridEntity(mapId);
            maps.SetTile(otherGrid, Vector2i.Zero, floor);
            Spawn(entities, otherGrid.Owner, "SMESBasic", 0);
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var state = ReadHull(entities, console);
            Assert.That(state.WorkingThrusters, Is.EqualTo(1), "An installed, unpowered engine is not working propulsion");
            Assert.That(state.WorkingGyroscopes, Is.EqualTo(1), "Angular thrusters must be reported separately from engines");
            Assert.That(state.IntegrityFraction, Is.EqualTo(1f));
            Assert.That(state.StoredEnergy,
                Is.EqualTo(storage.Sum(uid => entities.GetComponent<BatteryComponent>(uid).CurrentCharge)).Within(1));
            Assert.That(state.EnergyCapacity,
                Is.EqualTo(storage.Sum(uid => entities.GetComponent<BatteryComponent>(uid).MaxCharge)).Within(1),
                "Only the ship's SMES, substation and APC storage contributes, excluding loose cells and other grids");
            var power = entities.System<PowerReceiverSystem>();
            power.SetPowerDisabled(engine, true);
            power.SetPowerDisabled(gyro, true);
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var state = ReadHull(pair.Server.EntMan, console);
            Assert.That(state.WorkingThrusters, Is.Zero);
            Assert.That(state.WorkingGyroscopes, Is.Zero);
            Assert.That(state.MissingThrusters, Is.Zero, "Power loss must not mark an installed engine as destroyed");
            Assert.That(state.IntegrityFraction, Is.EqualTo(1f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemovingTheLastFloorTileUnderAConsoleClearsItsHullState()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        EntityUid console = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var grid = maps.CreateGridEntity(mapId);
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(grid, Vector2i.Zero, floor);
            console = Spawn(entities, grid.Owner, "ComputerShuttle", 0);
            ReadHull(entities, console);
            maps.SetTile(grid, Vector2i.Zero, Tile.Empty);
            Assert.That(entities.GetComponent<TransformComponent>(console).Anchored, Is.False);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var ui = pair.Server.EntMan.GetComponent<UserInterfaceComponent>(console);
            var state = (ShuttleBoundUserInterfaceState) ui.States[ShuttleConsoleUiKey.Key];
            Assert.That(state.HullState, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HullOnlyScansWhileStatusIsViewedAndResetClearsWeaponGhosts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        EntityUid console = default, grid = default, actor = default, secondActor = default;
        ShuttleHullState paused = null!;
        System.Collections.Generic.Dictionary<EntityUid, ShuttleHullState> Cache(IEntityManager entities) =>
            (System.Collections.Generic.Dictionary<EntityUid, ShuttleHullState>) typeof(ShuttleConsoleSystem)
                .GetField("_hullStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(entities.System<ShuttleConsoleSystem>())!;
        void View(IEntityManager entities, EntityUid viewer, bool active) => entities.EventBus.RaiseLocalEvent(console,
            new ShuttleHullViewMessage(active) { Actor = viewer, UiKey = ShuttleConsoleUiKey.Key });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.CreateMap(out var mapId);
            var created = maps.CreateGridEntity(mapId);
            grid = created.Owner;
            var floor = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 4; x++)
                maps.SetTile(created, new Vector2i(x, 0), floor);
            console = Spawn(entities, grid, "ComputerShuttle", 0);
            actor = Spawn(entities, grid, "MobHuman", 0);
            secondActor = Spawn(entities, grid, "MobHuman", 0);
            entities.System<PowerReceiverSystem>().SetNeedsPower(console, false);
            entities.RemoveComponent<ShuttleConsoleLockComponent>(console);
            entities.RemoveComponent<Content.Shared.Access.Components.AccessReaderComponent>(console);
            var gun = Spawn(entities, grid, "ShuttleGunKinetic", 2);
            entities.DeleteEntity(gun);
            var shuttle = entities.System<ShuttleConsoleSystem>();
            shuttle.RefreshState(console);
            Assert.That(Cache(entities).ContainsKey(grid), Is.False, "A normal console refresh must not scan the hull");
            Assert.That(shuttle.GetDestroyedWeapons(grid), Is.EqualTo(new[] { new Vector2(2.5f, 0.5f) }),
                "Destroyed gun positions must be retained without opening the status tab");
            var fireState = entities.System<Content.Server._Mono.FireControl.FireControlSystem>().GetConsoleState(console)!;
            Assert.That(fireState.DestroyedWeapons, Has.Length.EqualTo(1));
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(Cache(entities).ContainsKey(grid), Is.False, "Idle ships must not receive periodic hull scans");
            var ui = entities.System<Robust.Server.GameObjects.UserInterfaceSystem>();
            ui.OpenUi(console, ShuttleConsoleUiKey.Key, actor);
            ui.OpenUi(console, ShuttleConsoleUiKey.Key, secondActor);
            Assert.That(ui.IsUiOpen(console, ShuttleConsoleUiKey.Key, actor), Is.True);
            entities.System<ShuttleConsoleSystem>().RefreshState(console);
            Assert.That(Cache(entities).ContainsKey(grid), Is.False, "Opening the navigation tab must not scan the hull");
            View(entities, actor, true);
            paused = Cache(entities)[grid];
            Assert.That(paused.MissingWeapons, Is.EqualTo(1));
            View(entities, actor, false);
            entities.System<SharedMapSystem>().SetTile(grid, entities.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid),
                new Vector2i(3, 0), Tile.Empty);
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(Cache(entities)[grid], Is.SameAs(paused), "Leaving status must stop scans even with the console open");
            View(entities, secondActor, true);
            Assert.That(Cache(entities)[grid].MissingTiles, Is.EqualTo(1), "Entering status must immediately capture current damage");
            View(entities, actor, true);
            entities.System<Robust.Server.GameObjects.UserInterfaceSystem>().CloseUi(console, ShuttleConsoleUiKey.Key, actor);
            paused = Cache(entities)[grid];
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(Cache(entities)[grid], Is.Not.SameAs(paused), "One viewer closing must not stop another viewer's scans");
            entities.EventBus.RaiseLocalEvent(console,
                new ShuttleHullResetMessage { Actor = secondActor, UiKey = ShuttleConsoleUiKey.Key });
            Assert.That(entities.System<ShuttleConsoleSystem>().GetDestroyedWeapons(grid), Is.Empty);
            Assert.That(Cache(entities)[grid].MissingWeapons, Is.Zero);
            entities.System<Robust.Server.GameObjects.UserInterfaceSystem>().CloseUi(console, ShuttleConsoleUiKey.Key, secondActor);
            paused = Cache(entities)[grid];
        });
        await pair.RunTicksSync(180);
        await pair.Server.WaitAssertion(() =>
            Assert.That(Cache(pair.Server.EntMan)[grid], Is.SameAs(paused), "Closing the last viewer must stop scanning"));
        await pair.CleanReturnAsync();
    }
    private static EntityUid Spawn(IEntityManager entities, EntityUid grid, string prototype, int x)
    {
        return entities.SpawnEntity(prototype, new EntityCoordinates(grid, new Vector2(x + 0.5f, 0.5f)));
    }

    private static ShuttleHullState ReadHull(IEntityManager entities, EntityUid console)
    {
        // Baseline/telemetry fixtures request a snapshot directly. Normal console refreshes
        // deliberately no longer scan the hull before the status tab is selected.
        var system = entities.System<ShuttleConsoleSystem>();
        var capture = typeof(ShuttleConsoleSystem).GetMethod("CaptureHull",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (ShuttleHullState) capture.Invoke(system, [entities.GetComponent<TransformComponent>(console).GridUid!.Value])!;
    }
}
