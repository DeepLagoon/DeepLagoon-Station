using System.Linq;
using System.Numerics;
using Content.Server.Access.Systems;
using Content.Server.Medical.SuitSensors;
using Content.Server.Power.EntitySystems;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Inventory;
using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage.Expeditions;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CrewMonitoringExpeditionTests
{
    [Test]
    public async Task MonitorKeepsStatusAndOnlyShowsGpsOnTheSameMap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var entities = pair.Server.EntMan;
        EntityUid sector = default, expedition = default, ship = default;
        EntityUid homeConsole = default, shipConsole = default, observer = default;
        NetEntity sensor = default;
        const string shipName = "Raven-710";
        const string crewName = "SAM";
        var shipPosition = new Vector2(8f, 0f);

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            sector = maps.CreateMap(out var sectorId);
            expedition = maps.CreateMap(out _);
            var mission = entities.AddComponent<SalvageExpeditionComponent>(expedition);
            mission.Stage = ExpeditionStage.Running;
            mission.EndTime = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(10);
            var stationGrid = maps.CreateGridEntity(sectorId);
            var shipGrid = maps.CreateGridEntity(sectorId);
            ship = shipGrid.Owner;
            entities.System<MetaDataSystem>().SetEntityName(ship, shipName);
            Move(ship, new EntityCoordinates(sector, shipPosition));

            var plating = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(stationGrid, Vector2i.Zero, plating);
            for (var x = 0; x < 5; x++)
                for (var y = 0; y < 3; y++)
                    maps.SetTile(shipGrid, new Vector2i(x, y), plating);

            var stationCoords = new EntityCoordinates(stationGrid.Owner, new Vector2(.5f, .5f));
            homeConsole = entities.SpawnEntity("ComputerCrewMonitoring", stationCoords);
            shipConsole = entities.SpawnEntity("ComputerCrewMonitoring", new EntityCoordinates(ship, new Vector2(.5f, .5f)));
            var server = entities.SpawnEntity("CrewMonitoringServer", stationCoords);
            var power = entities.System<PowerReceiverSystem>();
            power.SetNeedsPower(server, false);
            power.SetNeedsPower(homeConsole, false);
            power.SetNeedsPower(shipConsole, false);

            var crewCoords = new EntityCoordinates(ship, new Vector2(3.5f, 1.5f));
            var synth = entities.SpawnEntity("MobSynth", crewCoords);
            var uniform = entities.SpawnEntity("ClothingUniformJumpsuitColorGrey", crewCoords);
            var id = entities.SpawnEntity("PassengerIDCard", crewCoords);
            entities.System<IdCardSystem>().TryChangeFullName(id, crewName);
            var inventory = entities.System<InventorySystem>();
            Assert.That(inventory.TryEquip(synth, uniform, "jumpsuit", force: true), Is.True);
            Assert.That(inventory.TryEquip(synth, id, "id", force: true), Is.True);
            entities.System<MobStateSystem>().ChangeMobState(synth, MobState.Dead);
            entities.System<SuitSensorSystem>()
                .SetSensor((uniform, entities.GetComponent<SuitSensorComponent>(uniform)), SuitSensorMode.SensorCords);
            sensor = entities.GetNetEntity(uniform);

            observer = entities.SpawnEntity("MobObserver", stationCoords);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), observer);
            entities.System<UserInterfaceSystem>().OpenUi(homeConsole, CrewMonitoringUIKey.Key, observer);
        });

        await pair.RunTicksSync(240);
        await pair.Server.WaitAssertion(() =>
        {
            AssertReading(homeConsole, shipName, hasGps: true);
            Move(ship, new EntityCoordinates(expedition, shipPosition));
        });

        await pair.RunTicksSync(240);
        await pair.Server.WaitAssertion(() =>
        {
            var expeditionLocation = Loc.GetString("crew-monitoring-location-expedition-shuttle", ("shuttle", shipName));
            Assert.That(expeditionLocation, Is.EqualTo($"{shipName} (экспедиция)"));
            AssertReading(homeConsole, expeditionLocation, hasGps: false);

            Move(observer, entities.GetComponent<TransformComponent>(shipConsole).Coordinates);
            entities.System<UserInterfaceSystem>().OpenUi(shipConsole, CrewMonitoringUIKey.Key, observer);
            AssertReading(shipConsole, shipName, hasGps: true);

            Move(ship, new EntityCoordinates(sector, shipPosition));
            Move(observer, entities.GetComponent<TransformComponent>(homeConsole).Coordinates);
            entities.System<UserInterfaceSystem>().OpenUi(homeConsole, CrewMonitoringUIKey.Key, observer);
        });

        await pair.RunTicksSync(240);
        await pair.Server.WaitAssertion(() => AssertReading(homeConsole, shipName, hasGps: true));
        await pair.CleanReturnAsync();

        void Move(EntityUid entity, EntityCoordinates coordinates)
        {
            entities.System<SharedTransformSystem>().SetCoordinates(entity, coordinates);
        }

        void AssertReading(EntityUid console, string location, bool hasGps)
        {
            var ui = entities.System<UserInterfaceSystem>();
            Assert.That(ui.TryGetUiState<CrewMonitoringState>(console, CrewMonitoringUIKey.Key, out var state), Is.True);
            var status = state!.Sensors.Single(status => status.SuitSensorUid == sensor);
            Assert.That(status.Name, Is.EqualTo(crewName));
            Assert.That(status.IsAlive, Is.False);
            Assert.That(status.TotalDamage, Is.Not.Null);
            Assert.That(status.LocationName, Is.EqualTo(location));
            Assert.That(status.Coordinates?.NetEntity, Is.EqualTo(hasGps ? entities.GetNetEntity(ship) : (NetEntity?) null));
        }
    }
}
