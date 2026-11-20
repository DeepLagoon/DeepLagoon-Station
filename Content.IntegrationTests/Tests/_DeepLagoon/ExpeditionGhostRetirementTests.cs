using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Performance;
using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Server.Salvage.Expeditions;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared._NF.CCVar;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ExpeditionGhostRetirementTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task RetiringExpeditionPreservesMindPaysOnceAndDrainsFollowingMaps(bool alreadyGhost)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true, Destructive = true, DummyTicker = false,
        });
        var server = pair.Server;
        var em = server.EntMan;
        var ticker = em.System<GameTicker>();
        await PoolManager.WaitUntil(server, () => ticker.RunLevel == GameRunLevel.InRound, 600);
        var home = await pair.CreateTestMap();
        EntityUid expeditionMap = default, followingMap = default, original = default, mindId = default;
        SalvageExpeditionDataComponent data = default!;
        MindComponent mind = default!;

        await server.WaitAssertion(() =>
        {
            server.ResolveDependency<IConfigurationManager>().SetCVar(NFCCVars.SalvageExpeditionRewardsEnabled, true);
            var maps = em.System<SharedMapSystem>();
            var station = em.SpawnEntity(null, MapCoordinates.Nullspace);
            em.AddComponent<StationDataComponent>(station);
            em.System<StationSystem>().AddGridToStation(station, home.Grid);
            data = em.AddComponent<SalvageExpeditionDataComponent>(station);
            data.ActiveMission = 1;
            data.CanFinish = true;
            var console = em.SpawnEntity("ComputerSalvageExpedition", home.GridCoords);
            var spawn = em.SpawnEntity(null, home.GridCoords);
            em.AddComponent<SpawnPointComponent>(spawn).SpawnType = SpawnPointType.Observer;

            expeditionMap = maps.CreateMap(out _);
            var mission = em.AddComponent<SalvageExpeditionComponent>(expeditionMap);
            mission.Station = station;
            mission.DispatchConsole = console;
            mission.MissionParams = new SalvageMissionParams { Index = 1, MissionType = SalvageMissionType.Elimination };
            mission.Completed = true;
            mission.Rewards = ["SpaceCashExpeditionT3"];
            mission.EndTime = server.ResolveDependency<IGameTiming>().CurTime + System.TimeSpan.FromMinutes(10);

            original = em.SpawnEntity(alreadyGhost ? "MobObserver" : "MobHuman",
                new EntityCoordinates(expeditionMap, Vector2.Zero));
            var minds = em.System<MindSystem>();
            mindId = minds.CreateMind(server.PlayerMan.Sessions.Single().UserId, "Retiring explorer");
            mind = em.GetComponent<MindComponent>(mindId);
            minds.TransferTo(mindId, original);

            followingMap = maps.CreateMap(out _);
            var deletion = em.System<StagedMapDeletionSystem>();
            deletion.Queue(expeditionMap);
            deletion.Queue(followingMap);
        });

        await pair.RunTicksSync(100);
        await server.WaitAssertion(() =>
        {
            var deletion = em.System<StagedMapDeletionSystem>();
            Assert.Multiple(() =>
            {
                Assert.That(em.EntityExists(expeditionMap), Is.False, "A respawning observer must not pin the retiring map.");
                Assert.That(em.EntityExists(followingMap), Is.False, "The next map must not starve behind the expedition.");
                Assert.That(deletion.PendingCount, Is.Zero);
                Assert.That(data.ActiveMission, Is.Zero);
                Assert.That(data.CanFinish, Is.False);
                Assert.That(data.Cooldown, Is.True);
                Assert.That(em.EntityExists(original), Is.False);
                Assert.That(em.EntityExists(mindId), Is.True);
                Assert.That(mind.OwnedEntity, Is.Not.Null);
            });
            var observer = mind.OwnedEntity!.Value;
            Assert.That(em.HasComponent<GhostComponent>(observer), Is.True);
            Assert.That(em.GetComponent<TransformComponent>(observer).MapUid, Is.Not.EqualTo(expeditionMap));
            Assert.That(server.PlayerMan.Sessions.Single().AttachedEntity, Is.EqualTo(observer));
            Assert.That(RewardCount(), Is.EqualTo(1));
        });

        await pair.RunTicksSync(20);
        await server.WaitAssertion(() => Assert.That(RewardCount(), Is.EqualTo(1), "Cleanup must not issue the reward twice."));
        await pair.CleanReturnAsync();

        int RewardCount()
        {
            var count = 0;
            var query = em.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out _, out var meta, out var xf))
                if (meta.EntityPrototype?.ID == "SpaceCashExpeditionT3" && xf.GridUid == home.Grid)
                    count++;
            return count;
        }
    }

    [TestCase(0)] // Grid-relative coordinates.
    [TestCase(1)] // Coordinates relative to a child of the grid.
    [TestCase(2)] // Map-relative coordinates spatially overlapping the grid.
    public async Task GhostSpawnAndObserverFallbackExcludeRetiringGrid(int coordinateKind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false, Destructive = true });
        var retiring = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var maps = em.System<SharedMapSystem>();
            var transforms = em.System<SharedTransformSystem>();
            var point = retiring.GridCoords;
            if (coordinateKind == 1)
                point = new EntityCoordinates(em.SpawnEntity(null, point), Vector2.Zero);
            else if (coordinateKind == 2)
                point = new EntityCoordinates(retiring.MapUid, Vector2.Zero);
            Assert.That(transforms.GetMap(point), Is.EqualTo(retiring.MapUid));
            var query = em.AllEntityQueryEnumerator<SpawnPointComponent>();
            while (query.MoveNext(out _, out var existing))
                existing.SpawnType = SpawnPointType.Unset;
            var spawn = em.SpawnEntity(null, point);
            em.AddComponent<SpawnPointComponent>(spawn).SpawnType = SpawnPointType.Observer;
            em.System<StagedMapDeletionSystem>().Queue(retiring.Grid.Owner);

            var initialFallback = em.System<GameTicker>().GetObserverSpawnPoint();
            if (initialFallback != EntityCoordinates.Invalid)
            {
                var overlapsRetiring = maps.TryFindGridAt(transforms.ToMapCoordinates(initialFallback), out var grid, out _)
                    && grid == retiring.Grid.Owner;
                Assert.That(overlapsRetiring, Is.False,
                    "A map-origin fallback must not silently attach a ghost to the retiring grid at that point.");
            }

            // Ensure a safe fallback exists even if the test world initially contained just this grid.
            maps.CreateMap(out _);
            var fallback = em.System<GameTicker>().GetObserverSpawnPoint();
            Assert.That(transforms.GetGrid(fallback), Is.Not.EqualTo(retiring.Grid.Owner),
                "Observer spawn points and grid fallbacks must exclude retiring grids.");

            var minds = em.System<MindSystem>();
            var mind = minds.CreateMind(null, "Offline retiring explorer");
            var ghost = em.System<GhostSystem>().SpawnGhost(mind, point);
            Assert.That(ghost, Is.Not.Null, "Retirement must preserve the offline mind in a safe observer.");
            Assert.That(em.GetComponent<TransformComponent>(ghost!.Value).GridUid, Is.Not.EqualTo(retiring.Grid.Owner));
            Assert.That(em.GetComponent<MindComponent>(mind).OwnedEntity, Is.EqualTo(ghost));
        });
        await pair.Server.WaitRunTicks(10);
        await pair.CleanReturnAsync();
    }
}
