using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.Gravity;
using Content.Server.Mind;
using Content.Server.NPC.Components;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._DeepLagoon.ShuttleParking;
using Content.Shared.Access.Components;
using Content.Shared.Atmos;
using Content.Shared.Bed.Sleep;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Station.Components;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class AutomaticShuttleParkingTests
{
    public enum ExitMode { Walking, Immobile, ClosedPort, Obstructed }

    [TestCase(ExitMode.Walking, TestName = "AutoParkingWalksOfflinePassengerThroughPortAndAllowsRecall")]
    [TestCase(ExitMode.Immobile, TestName = "AutoParkingTransfersImmobileOfflinePassenger")]
    [TestCase(ExitMode.ClosedPort, TestName = "AutoParkingTransfersOfflinePassengerPastClosedAirlock")]
    [TestCase(ExitMode.Obstructed, TestName = "AutoParkingTransfersOfflinePassengerWhenWalkingFails")]
    public async Task AutoParkingRequiresContinuousOfflineDockingAndAllowsOwnerRecall(ExitMode exit)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Destructive = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var entities = server.EntMan;
        var owner = pair.Player!;
        var crew = await server.AddDummySession("ParkingCrew");
        var ticker = server.System<GameTicker>();
        await PoolManager.WaitUntil(server, () => ticker.UserHasJoinedGame(crew), 600);
        var net = pair.Client.ResolveDependency<IClientNetManager>();
        var docking = entities.System<DockingSystem>();
        EntityUid stationGrid = default, ship = default, dock = default, shipDock = default, console = default, shipStation = default;
        EntityUid body = default, card = default;
        ParkingShuttleComponent parking = null!;

        void Dock()
        {
            var shipTransform = entities.GetComponent<TransformComponent>(ship);
            // Keep the test shuttle clear of the docking volume while requesting a docking configuration.
            entities.System<SharedTransformSystem>().SetCoordinates(ship,
                new EntityCoordinates(shipTransform.MapUid!.Value, new Vector2(16, 0)));
            var config = docking.GetDockingConfig(ship, stationGrid, shipDock, entities.GetComponent<DockingComponent>(shipDock),
                dock, entities.GetComponent<DockingComponent>(dock));
            Assert.That(config, Is.Not.Null);
            entities.System<ShuttleSystem>().FTLDock((ship, shipTransform), config!);
            // The synthetic grids have no APCs to operate their docking airlocks.
            var doors = entities.System<SharedDoorSystem>();
            if (exit != ExitMode.ClosedPort)
            {
                doors.StartOpening(dock);
                doors.StartOpening(shipDock);
            }
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(server.CfgMan.GetCVar(ShuttleParkingCVars.AutoDebugSeconds), Is.Zero);
            server.CfgMan.SetCVar(ShuttleParkingCVars.AutoDebugSeconds, 3);
            server.CfgMan.SetCVar(Robust.Shared.CVars.GameAutoPauseEmpty, false);
            var maps = entities.System<SharedMapSystem>();
            var map = maps.CreateMap(out var mapId);
            entities.EnsureComponent<GravityComponent>(map);
            entities.System<GravitySystem>().EnableGravity(map);
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.82f;
            moles[(int) Gas.Nitrogen] = 82.10f;
            entities.System<AtmosphereSystem>().SetMapAtmosphere(map, false, new GasMixture(moles, Atmospherics.T20C));

            var stationEnt = maps.CreateGridEntity(mapId);
            var shipEnt = maps.CreateGridEntity(mapId);
            stationGrid = stationEnt.Owner;
            ship = shipEnt.Owner;
            entities.EnsureComponent<ShuttleComponent>(ship);
            var floor = new Tile(server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
                maps.SetTile(shipEnt, new Vector2i(x, y), floor);
            for (var x = 0; x < 6; x++)
            for (var y = 0; y < 3; y++)
                maps.SetTile(stationEnt, new Vector2i(x, y), floor);

            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<StationDataComponent>(station);
            entities.EnsureComponent<StationAutomaticShuttleParkingComponent>(station);
            shipStation = entities.SpawnEntity("StandardFrontierVessel", MapCoordinates.Nullspace);
            var stations = entities.System<StationSystem>();
            stations.AddGridToStation(station, stationGrid);
            stations.AddGridToStation(shipStation, ship);
            var jobs = entities.System<StationJobsSystem>();
            Assert.That(jobs.TryAdjustJobSlot(shipStation, "Contractor", 1, createSlot: true), Is.True);
            Assert.That(jobs.TryAssignJob(shipStation, "Contractor", crew.UserId), Is.True);

            var arrival = new EntityCoordinates(stationGrid, new Vector2(.5f, 1.5f));
            entities.SpawnEntity("SpawnPointLatejoin", arrival);
            console = entities.SpawnEntity("ComputerShuttleParking", new EntityCoordinates(stationGrid, new Vector2(2.5f, .5f)));
            dock = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(stationGrid, new Vector2(5.5f, 1.5f)));
            shipDock = entities.SpawnEntity("AirlockShuttle", new EntityCoordinates(ship, new Vector2(.5f, 1.5f)));
            var transforms = entities.System<SharedTransformSystem>();
            transforms.SetLocalRotation(dock, Angle.FromDegrees(90));
            transforms.SetLocalRotation(shipDock, Angle.FromDegrees(270));
            Dock();

            var shipInterior = new EntityCoordinates(ship, new Vector2(1.5f, 1.5f));
            body = entities.SpawnEntity("MobHuman", shipInterior);
            var minds = entities.System<MindSystem>();
            minds.TransferTo(minds.GetOrCreateMind(owner.UserId).Owner, body);
            entities.System<ParkingSystem>().RegisterPurchasedShip(ship, owner.UserId.UserId, 1, "", "Captain");
            parking = entities.GetComponent<ParkingShuttleComponent>(ship);
            card = entities.SpawnEntity("PassengerIDCard", arrival);
            entities.GetComponent<IdCardComponent>(card).FullName = entities.GetComponent<MetaDataComponent>(body).EntityName;
            entities.System<ShipyardSystem>().BindParkingDeed(ship, card, "Test shuttle", "Captain");
            var slot = entities.GetComponent<ParkingConsoleComponent>(console).Card.ContainerSlot!;
            Assert.That(entities.System<SharedContainerSystem>().Insert(card, slot), Is.True);
        });

        await pair.RunSeconds(4);
        await server.WaitAssertion(() => Assert.That(parking.Parked, Is.False, "Owner is still online"));

        await pair.Client.WaitPost(() => net.ClientDisconnect("Testing offline parking"));
        await pair.RunSeconds(4);
        await server.WaitAssertion(() => Assert.That(parking.Parked, Is.False, "Assigned crew is online off the ship"));
        var visitor = crew.AttachedEntity!.Value;
        var visitorPosition = EntityCoordinates.Invalid;
        await server.WaitAssertion(() =>
        {
            var transforms = entities.System<SharedTransformSystem>();
            visitorPosition = entities.GetComponent<TransformComponent>(visitor).Coordinates;
            Assert.That(entities.System<StationJobsSystem>().TryRemovePlayerJobs(shipStation, crew.UserId), Is.True);
            transforms.SetCoordinates(visitor, new EntityCoordinates(ship, new Vector2(1.5f, .5f)));
        });
        await pair.RunSeconds(4);
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.Parked, Is.False, "An online visitor aboard blocks auto parking");
            Assert.That(entities.HasComponent<NPCSteeringComponent>(body), Is.False);
            entities.System<SharedTransformSystem>().SetCoordinates(visitor, visitorPosition);
        });
        await server.RemoveDummySession(crew);

        await pair.RunSeconds(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.Parked, Is.False, "The delay has not elapsed");
            docking.UndockDocks(ship);
        });
        await pair.RunSeconds(4);
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.Parked, Is.False, "An undocked shuttle must stay in the sector");
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(ship));
            Dock();
            if (exit == ExitMode.Immobile)
                entities.RemoveComponent<InputMoverComponent>(body);
            if (exit == ExitMode.Obstructed)
            {
                entities.System<SharedTransformSystem>().SetCoordinates(body, new EntityCoordinates(ship, new Vector2(2.5f, 1.5f)));
                for (var y = 0; y < 3; y++)
                    entities.SpawnEntity("WallSolid", new EntityCoordinates(ship, new Vector2(1.5f, y + .5f)));
            }

            entities.EnsureComponent<ForcedSleepingComponent>(body);
            Assert.That(entities.HasComponent<SleepingComponent>(body), Is.True);
        });
        await pair.RunSeconds(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.Parked, Is.False, "Redocking starts a fresh delay");
            if (exit == ExitMode.ClosedPort)
                Assert.That(entities.GetComponent<DoorComponent>(shipDock).State, Is.EqualTo(DoorState.Closed));
        });
        if (exit is ExitMode.Walking or ExitMode.Obstructed)
        {
            await PoolManager.WaitUntil(server, () => entities.HasComponent<NPCSteeringComponent>(body), pair.SecondsToTicks(5));
            await server.WaitAssertion(() =>
            {
                Assert.That(parking.Parked, Is.False);
                Assert.That(entities.HasComponent<SleepingComponent>(body), Is.False, "The sleeper must wake to walk");
                Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(ship), "Evacuation starts aboard");
            });
        }

        await pair.RunSeconds(12);
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.Parked, Is.True);
            Assert.That(parking.AutoParkingNoticePending, Is.True);
            Assert.That(entities.GetComponent<MetaDataComponent>(ship).EntityPaused, Is.True);
            Assert.That(entities.GetComponent<DockingComponent>(dock).DockedWith, Is.Null);
            Assert.That(entities.GetComponent<TransformComponent>(body).GridUid, Is.EqualTo(stationGrid));
            Assert.That(entities.HasComponent<NPCSteeringComponent>(body), Is.False);
            if (exit is ExitMode.Immobile or ExitMode.ClosedPort)
                Assert.That(entities.HasComponent<SleepingComponent>(body), Is.True, "Direct transfer preserves sleep");
            var transforms = entities.System<SharedTransformSystem>();
            var distance = Vector2.Distance(transforms.GetWorldPosition(body), transforms.GetWorldPosition(dock));
            Assert.That(distance, exit == ExitMode.Walking ? Is.InRange(2.8f, 4.2f) : Is.InRange(3f, 4f),
                "The passenger stops clear of the actual station docking port");
        });

        pair.Client.SetConnectTarget(server);
        await pair.Client.WaitPost(() => net.ClientConnect(null!, 0, owner.Name));
        await pair.RunSeconds(2);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var loc = pair.Client.ResolveDependency<ILocalizationManager>();
            var notice = ui.WindowRoot.Children.OfType<DefaultWindow>()
                .Single(window => window.Title == loc.GetString("shuttle-auto-parking-title"));
            notice.Close();
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(parking.AutoParkingNoticePending, Is.False);
            Assert.That(server.PlayerMan.Sessions.Single().AttachedEntity, Is.EqualTo(body));
            entities.GetComponent<ApcPowerReceiverComponent>(console).Powered = true;
            entities.EventBus.RaiseLocalEvent(console, new ParkingAction("recall", dock: entities.GetNetEntity(dock))
                { Actor = body, UiKey = ParkingUiKey.Key });
            Assert.That(parking.Parked, Is.False);
            Assert.That(entities.GetComponent<DockingComponent>(dock).DockedWith, Is.EqualTo(shipDock));
        });

        await pair.CleanReturnAsync();
    }
}
