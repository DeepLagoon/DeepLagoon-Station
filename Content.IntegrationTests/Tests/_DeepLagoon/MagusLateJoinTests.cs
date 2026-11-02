using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Preferences;
using Content.Shared.GameTicking;
using Content.Shared.Players;
using Content.Shared.Station.Components;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class MagusLateJoinTests
{
    [Test]
    public async Task MissingSpawnPointLeavesPlayerInLobbyWithoutReplacingMind()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var ticker = entities.System<GameTicker>();
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationSpawningComponent>(station);
            var player = pair.Player!;
            var mind = player.ContentData()!.Mind;
            var status = ticker.PlayerGameStatuses[player.UserId];
            Assert.That(status, Is.Not.EqualTo(PlayerGameStatus.JoinedGame));
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            try
            {
                // A missing spawn point deliberately logs an error; the join must still return safely.
                pair.ServerLogHandler.FailureLevel = null;
                Assert.DoesNotThrow(() => ticker.MakeJoinGame(player, station, "Contractor"));
            }
            finally
            {
                pair.ServerLogHandler.FailureLevel = failureLevel;
            }
            Assert.That(ticker.PlayerGameStatuses[player.UserId], Is.EqualTo(status));
            Assert.That(player.ContentData()!.Mind, Is.EqualTo(mind));
            Assert.That(player.AttachedEntity, Is.Null);
        });
        await pair.RunTicksSync(5);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MagusCanSpawnCrewOnItsOwnGrid()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<MapSystem>();
            var map = maps.CreateMap(out var mapId);
            Assert.That(entities.System<MapLoaderSystem>().TryLoadGrid(mapId,
                new ResPath("/SharedMaps/_Mono/Shuttles/Expedition/magus.yml"), out var loaded), Is.True);
            var grid = loaded!.Value.Owner;
            var station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<StationDataComponent>(station);
            entities.AddComponent<StationSpawningComponent>(station);
            var stations = entities.System<StationSystem>();
            stations.AddGridToStation(station, grid);

            var points = entities.EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
            var hasLateJoin = false;
            while (points.MoveNext(out var uid, out var point, out var transform))
            {
                if (transform.GridUid == grid && point.SpawnType == SpawnPointType.LateJoin)
                {
                    Assert.That(stations.GetOwningStation(uid), Is.EqualTo(station));
                    hasLateJoin = true;
                }
            }
            Assert.That(hasLateJoin, Is.True, "Magus must provide a late-join point for the Crew picker.");
            var mob = entities.System<StationSpawningSystem>().SpawnPlayerCharacterOnStation(station,
                "Contractor", HumanoidCharacterProfile.DefaultWithSpecies("Human"));
            Assert.That(mob, Is.Not.Null);
            Assert.That(entities.GetComponent<TransformComponent>(mob!.Value).GridUid, Is.EqualTo(grid));
            Assert.That(entities.GetComponent<TransformComponent>(mob.Value).MapUid, Is.EqualTo(map));
        });
        await pair.CleanReturnAsync();
    }
}
