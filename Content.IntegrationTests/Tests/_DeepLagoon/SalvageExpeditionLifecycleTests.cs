using System.Numerics;
using System.Linq;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Systems;
using Content.Shared.Salvage;
using Content.Shared._NF.CCVar;
using Content.Shared.Cargo.Components;
using Content.Shared.CCVar;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Station.Components;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using ClientExpedition = Content.Client.Salvage.SalvageExpeditionComponent;
using ClientSalvage = Content.Client.Salvage.SalvageSystem;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SalvageExpeditionLifecycleTests
{
    [Test]
    public async Task ExpeditionRecordsIndependentDeparturePointsAndHeadings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var firstOrigin = maps.CreateMap(out _);
            var secondOrigin = maps.CreateMap(out _);
            var destination = maps.CreateMap(out var destinationId);
            var expedition = entities.AddComponent<SalvageExpeditionComponent>(destination);

            void Enter(EntityUid origin, Vector2 position, Angle heading)
            {
                var ship = maps.CreateGridEntity(destinationId).Owner;
                var ev = new FTLStartedEvent(ship, new EntityCoordinates(destination, Vector2.Zero),
                    origin, Matrix3x2.CreateTranslation(position), heading);
                entities.EventBus.RaiseLocalEvent(ship, ref ev, true);
                Assert.That(expedition.ReturnLocations[ship], Is.EqualTo((new EntityCoordinates(origin, position), heading)));
            }

            Enter(firstOrigin, new Vector2(7000f, -3000f), new Angle(0.5));
            Enter(secondOrigin, new Vector2(-18000f, 2500f), new Angle(1.5));
            Assert.That(expedition.ReturnLocations, Has.Count.EqualTo(2));
            entities.DeleteEntity(destination);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WeatherCanBeDisabledAndReenabled()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var salvage = pair.Server.EntMan.System<SharedSalvageSystem>();
            config.SetCVar(CCVars.SalvageExpeditionWeatherEnabled, false);
            for (var seed = 0; seed < 100; seed++)
            {
                var mission = salvage.GetMission(SalvageMissionType.Elimination, DifficultyRating.Extreme, seed);
                Assert.That(mission.Weather, Is.EqualTo("ClearWeather"));
            }

            config.SetCVar(CCVars.SalvageExpeditionWeatherEnabled, true);
            var hasWeather = false;
            for (var seed = 0; seed < 100; seed++)
            {
                var mission = salvage.GetMission(SalvageMissionType.Elimination, DifficultyRating.Extreme, seed);
                hasWeather |= mission.Weather != "ClearWeather";
            }

            Assert.That(hasWeather, Is.True, "Enabling weather must restore the expedition weather modifiers.");
            config.SetCVar(CCVars.SalvageExpeditionWeatherEnabled, false);
            Assert.That(salvage.GetMission(SalvageMissionType.Elimination, DifficultyRating.Extreme, 0).Weather,
                Is.EqualTo("ClearWeather"));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false, true, true, 7500)]
    [TestCase(false, true, false, 0)]
    [TestCase(false, false, true, 0)]
    [TestCase(true, true, false, 5, TestName = "OutpostRewardNoticeFollowsActualDelivery")]
    public async Task RewardNoticeFollowsActualDelivery(bool outpost, bool completed, bool cashEnabled, int issued)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        EntityUid expeditionMap = default;
        EntityUid home = default;
        EntityUid ship = default;
        EntityUid console = default;
        var amount = outpost ? 5 : 7500;
        var rewardPrototype = outpost ? "Telecrystal" : "SpaceCashExpeditionT3";

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(NFCCVars.SalvageExpeditionRewardsEnabled, cashEnabled);
            home = maps.CreateMap(out var homeId);
            expeditionMap = maps.CreateMap(out var expeditionId);
            var shipGrid = maps.CreateGridEntity(expeditionId);
            ship = shipGrid.Owner;
            var consoleGrid = outpost ? maps.CreateGridEntity(homeId) : shipGrid;
            var station = consoleGrid.Owner;
            if (outpost)
            {
                station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
                entities.AddComponent<StationDataComponent>(station);
                entities.System<StationSystem>().AddGridToStation(station, consoleGrid.Owner);
            }

            var data = entities.AddComponent<SalvageExpeditionDataComponent>(station);
            data.ActiveMission = 1;
            var plating = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            maps.SetTile(consoleGrid, Vector2i.Zero, plating);
            maps.SetTile(shipGrid, Vector2i.Zero, plating);
            console = entities.SpawnEntity(outpost ? "ComputerTabletopUndergroundExpeditions" : "ComputerSalvageExpedition",
                new EntityCoordinates(consoleGrid, new Vector2(.5f, .5f)));

            var mission = entities.AddComponent<SalvageExpeditionComponent>(expeditionMap);
            mission.Station = station;
            mission.DispatchConsole = console;
            mission.MissionParams = new SalvageMissionParams { Index = 1, MissionType = SalvageMissionType.Elimination };
            mission.Difficulty = DifficultyRating.Moderate;
            mission.DispatchedFromOutpost = outpost;
            mission.Rewards = ["SpaceCashExpeditionT3"];
            mission.Stage = ExpeditionStage.Running;
            mission.EndTime = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromMinutes(10);
            mission.ReturnLocations[ship] = (new EntityCoordinates(home, Vector2.Zero), Angle.Zero);

            var crew = entities.SpawnEntity("MobObserver", new EntityCoordinates(ship, new Vector2(.5f, .5f)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), crew);
        });

        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var player = pair.Client.PlayerMan.LocalEntity!.Value;
            var map = entities.GetComponent<TransformComponent>(player).MapUid!.Value;
            Assert.That(entities.GetComponent<ClientExpedition>(map).RewardAmount, Is.EqualTo(amount));
            Assert.That(entities.System<ClientSalvage>().ExpeditionRewardNotice, Is.Null);
        });

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            // Keep the returning ship clear of the outpost's delivery tile.
            entities.System<SharedTransformSystem>().SetCoordinates(ship, new EntityCoordinates(home, new Vector2(100f, 0f)));
            entities.GetComponent<SalvageExpeditionComponent>(expeditionMap).Completed = completed;
            entities.DeleteEntity(expeditionMap);

            var rewards = entities.EntityQuery<MetaDataComponent>().Where(m => m.EntityPrototype?.ID == rewardPrototype).ToArray();
            Assert.That(rewards, Has.Length.EqualTo(issued > 0 ? 1 : 0));
            if (issued > 0)
            {
                var reward = rewards.Single().Owner;
                Assert.That(entities.GetComponent<TransformComponent>(reward).Coordinates,
                    Is.EqualTo(entities.GetComponent<TransformComponent>(console).Coordinates));
                var value = outpost ? entities.GetComponent<StackComponent>(reward).Count
                    : (int) entities.GetComponent<StaticPriceComponent>(reward).Price;
                Assert.That(value, Is.EqualTo(issued));
            }
        });

        await pair.RunTicksSync(20);
        await pair.Client.WaitAssertion(() =>
        {
            var notice = pair.Client.EntMan.System<ClientSalvage>().ExpeditionRewardNotice;
            Assert.That(notice, issued > 0 ? Is.Not.Null : Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task DepartureIgnoresUnrelatedGridsButWaitsForStationShuttles(bool anotherStationShuttle, bool completed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var stations = entities.System<StationSystem>();
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            var data = entities.AddComponent<SalvageExpeditionDataComponent>(station);
            data.ActiveMission = 1;
            data.CanFinish = true;

            var expeditionMap = maps.CreateMap(out var expeditionId);
            var expedition = entities.AddComponent<SalvageExpeditionComponent>(expeditionMap);
            expedition.Station = station;
            expedition.MissionParams = new SalvageMissionParams { Index = 1, MissionType = SalvageMissionType.Elimination };
            expedition.Completed = completed;
            expedition.Rewards = [];

            // Both debris and another station's ship have ShuttleComponent.
            var debris = maps.CreateGridEntity(expeditionId);
            var foreignShip = maps.CreateGridEntity(expeditionId);
            var foreignStation = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(foreignStation);
            stations.AddGridToStation(foreignStation, foreignShip.Owner);

            var destination = maps.CreateMap(out var destinationId);
            var departed = maps.CreateGridEntity(destinationId);
            stations.AddGridToStation(station, departed.Owner);
            EntityUid? remaining = null;
            if (anotherStationShuttle)
            {
                remaining = maps.CreateGridEntity(expeditionId).Owner;
                stations.AddGridToStation(station, remaining.Value);
            }

            void Depart(EntityUid shuttle)
            {
                var ev = new FTLStartedEvent(shuttle, new EntityCoordinates(destination, Vector2.Zero),
                    expeditionMap, Matrix3x2.Identity, Angle.Zero);
                entities.EventBus.RaiseLocalEvent(shuttle, ref ev, true);
            }

            Depart(departed.Owner);
            Assert.That(entities.IsQueuedForDeletion(expeditionMap), Is.EqualTo(!anotherStationShuttle));
            if (remaining != null)
            {
                Assert.That(data.Claimed, Is.True, "Another ship of the expedition's station is still present.");
                entities.System<SharedTransformSystem>().SetCoordinates(remaining.Value,
                    new EntityCoordinates(destination, Vector2.Zero));
                Depart(remaining.Value);
            }

            Assert.That(entities.IsQueuedForDeletion(expeditionMap), Is.True,
                "Debris and another station's ship must not keep the expedition active.");
            entities.DeleteEntity(expeditionMap);
            Assert.Multiple(() =>
            {
                Assert.That(data.Claimed, Is.False);
                Assert.That(data.CanFinish, Is.False);
                Assert.That(data.Cooldown, Is.True);
            });
        });
        await pair.CleanReturnAsync();
    }
}
