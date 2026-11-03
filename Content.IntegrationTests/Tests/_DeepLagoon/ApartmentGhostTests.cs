using System.Numerics;
using Content.Server._DeepLagoon.Apartments;
using Content.Server.Spawners.Components;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Ghost;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentGhostTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task OrdinaryGhostsReturnButAdminGhostsAndBodiesRemain(bool paused, bool hub)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var outside = await pair.CreateTestMap();
        EntityUid map = default, ordinary = default, moved = default, admin = default, body = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            map = maps.CreateMap(runMapInit: true);
            if (hub)
                entities.AddComponent<ApartmentGhostRestrictedMapComponent>(map);
            else
                entities.AddComponent<ApartmentIsolationComponent>(map);
            maps.SetPaused(map, paused);
            var inside = new EntityCoordinates(map, new Vector2(100, 100));
            ordinary = entities.SpawnEntity("MobObserver", inside);
            moved = entities.SpawnEntity("MobObserver", outside.GridCoords);
            admin = entities.SpawnEntity("AdminObserver", inside);
            body = entities.SpawnEntity("MobHuman", inside);
            // Following reparents the ghost and changes its map without a ghost-specific warp.
            entities.System<FollowerSystem>().StartFollowingEntity(moved, body);
            var spawn = entities.SpawnEntity(null, outside.GridCoords);
            entities.AddComponent<SpawnPointComponent>(spawn).SpawnType = SpawnPointType.Observer;
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<TransformComponent>(ordinary).MapUid, Is.EqualTo(outside.MapUid));
            Assert.That(entities.GetComponent<TransformComponent>(moved).MapUid, Is.EqualTo(outside.MapUid));
            Assert.That(entities.HasComponent<FollowerComponent>(moved), Is.False);
            Assert.That(entities.GetComponent<TransformComponent>(admin).MapUid, Is.EqualTo(map));
            Assert.That(entities.GetComponent<TransformComponent>(body).MapUid, Is.EqualTo(map));
            Assert.That(entities.System<SharedMapSystem>().IsPaused(map), Is.EqualTo(paused));
            // Revoking the admin-ghost mode must also revoke its residential exception.
#pragma warning disable RA0002 // Exercise the VV-writable ghost interaction property.
            entities.GetComponent<GhostComponent>(admin).CanGhostInteract = false;
#pragma warning restore RA0002
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(admin).MapUid, Is.EqualTo(outside.MapUid)));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ProtectedGridAndRestrictedObserverSpawnCannotTrapGhost()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var home = await pair.CreateTestMap();
        EntityUid outside = default, ghost = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.AddComponent<ApartmentProtectedGridComponent>(home.Grid.Owner);
            outside = entities.System<SharedMapSystem>().CreateMap(runMapInit: true);
            // Even an explicit observer spawn inside the forbidden grid must be rejected.
            var spawn = entities.SpawnEntity(null, home.GridCoords);
            entities.AddComponent<SpawnPointComponent>(spawn).SpawnType = SpawnPointType.Observer;
            ghost = entities.SpawnEntity("MobObserver", home.GridCoords);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.GetComponent<TransformComponent>(ghost).MapUid, Is.EqualTo(outside)));
        await pair.CleanReturnAsync();
    }
}
