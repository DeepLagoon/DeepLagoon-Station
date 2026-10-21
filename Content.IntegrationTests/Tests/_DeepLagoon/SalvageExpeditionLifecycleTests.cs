using System.Numerics;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Systems;
using Content.Shared.Salvage;
using Content.Shared.CCVar;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SalvageExpeditionLifecycleTests
{
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
