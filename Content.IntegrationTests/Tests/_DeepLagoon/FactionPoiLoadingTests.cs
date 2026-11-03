using System.Collections.Generic;
using Content.Server._DeepLagoon.Mapping;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Content.Shared.Roles;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class FactionPoiLoadingTests
{
    [TestCase("HeliosFortress", "pdvhelios", "PirateCaptain")]
    [TestCase("TSFMCHalcyon", "tsfmchalcyon", "Sheriff")]
    [TestCase("Underground", "underground", "Godfather")]
    public async Task FactionGridCreatesStationJobsAndKeepsParking(string prototype, string file, string job)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            // Verify the loader's false result even when the original wrong-category
            // resource logs an error; the assertion must catch this regression.
            pair.ServerLogHandler.FailureLevel = null;
            var entities = pair.Server.EntMan;
            var map = entities.System<SharedMapSystem>().CreateMap(out var mapId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGridTracked(mapId,
                new ResPath($"/Maps/_Mono/POI/{file}.yml"), out var grid), Is.True);
            Assert.That(grid, Is.Not.Null);
            var stationData = pair.Server.ResolveDependency<IPrototypeManager>().Index<GameMapPrototype>(prototype).Stations[prototype];
            var station = entities.System<StationSystem>().InitializeNewStation(stationData,
                new List<EntityUid> { grid.Value.Owner }, prototype);
            Assert.That(entities.System<StationJobsSystem>().GetAvailableJobs(station), Does.Contain(new ProtoId<JobPrototype>(job)));
            var parking = entities.EntityQueryEnumerator<ParkingConsoleComponent, TransformComponent>();
            var found = false;
            while (parking.MoveNext(out _, out _, out var transform))
                found |= transform.GridUid == grid.Value.Owner;
            Assert.That(found, Is.True, "Parking added to the outpost must survive the Grid-format correction.");
            entities.DeleteEntity(station);
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }
}
