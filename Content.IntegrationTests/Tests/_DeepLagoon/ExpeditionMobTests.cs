using System.Numerics;
using Content.Server.Salvage.Expeditions;
using Content.Server.Storage.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ExpeditionMobTests
{
    [TestCase("MobMouse", false)]
    [TestCase("MobHuman", true)]
    [TestCase("NFMobXenoQueen", true)]
    [TestCase("MobRogueSiliconBoss", false)]
    [TestCase("MobCleanBot", false)]
    public async Task NativeMobsBurstOnDirectMapExit(string prototype, bool dead)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var expedition = await pair.CreateTestMap();
        var home = await pair.CreateTestMap();
        EntityUid mob = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            MarkExpedition(entities, expedition.MapUid);
            mob = entities.SpawnEntity(prototype, expedition.GridCoords);
            if (dead)
                entities.System<MobStateSystem>().ChangeMobState(mob, MobState.Dead);

            entities.System<SharedTransformSystem>().SetCoordinates(mob, home.GridCoords);
            Assert.That(entities.EntityExists(mob), Is.True, "Gibbing must wait until the transform move finishes.");
        });
        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.EntityExists(mob), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShuttleAndNestedContainerExitBurstsNativesAndPreservesVisitorsAndLoot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var expedition = await pair.CreateTestMap();
        var home = await pair.CreateTestMap();
        EntityUid native = default, lateSpawn = default, visitor = default, crew = default, crate = default, loot = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var transforms = entities.System<SharedTransformSystem>();
            MarkExpedition(entities, expedition.MapUid);
            visitor = entities.SpawnEntity("MobMouse", home.GridCoords);
            crew = entities.SpawnEntity("MobHuman", home.GridCoords);
            transforms.SetCoordinates(home.Grid, new EntityCoordinates(expedition.MapUid, new Vector2(10, 10)));
            native = entities.SpawnEntity("NFMobXeno", expedition.GridCoords);
            entities.System<MobStateSystem>().ChangeMobState(native, MobState.Dead);
            crate = entities.SpawnEntity("CrateGenericSteel", home.GridCoords);
            loot = entities.SpawnEntity("SheetSteel1", home.GridCoords);
            var storage = entities.GetComponent<EntityStorageComponent>(crate).Contents;
            var containers = entities.System<SharedContainerSystem>();
            Assert.That(containers.Insert(native, storage), Is.True);
            Assert.That(containers.Insert(loot, storage), Is.True);
            // Covers later spawns and mobs created aboard the expedition's shuttle.
            lateSpawn = entities.SpawnEntity("MobMouse", home.GridCoords);
        });
        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.EntityExists(native), Is.True, "Boarding a shuttle within the expedition is allowed.");
            Assert.That(entities.EntityExists(lateSpawn), Is.True);
            entities.System<SharedTransformSystem>().SetCoordinates(home.Grid, new EntityCoordinates(home.MapUid, Vector2.Zero));
        });
        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.Multiple(() =>
            {
                Assert.That(entities.EntityExists(native), Is.False);
                Assert.That(entities.EntityExists(lateSpawn), Is.False);
                Assert.That(entities.EntityExists(visitor), Is.True);
                Assert.That(entities.EntityExists(crew), Is.True);
                Assert.That(entities.EntityExists(crate), Is.True);
                Assert.That(entities.EntityExists(loot), Is.True);
            });
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeMobsCannotEscapeThroughAnotherExpeditionOrNullspace(bool nullspace)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        var expedition = await pair.CreateTestMap();
        var other = await pair.CreateTestMap();
        EntityUid mob = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            MarkExpedition(entities, expedition.MapUid);
            MarkExpedition(entities, other.MapUid);
            mob = entities.SpawnEntity("MobMouse", expedition.GridCoords);
            var transforms = entities.System<SharedTransformSystem>();
            if (nullspace)
                transforms.DetachEntity(mob, entities.GetComponent<TransformComponent>(mob));
            else
                transforms.SetCoordinates(mob, other.GridCoords);
        });
        await pair.RunTicksSync(3);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.EntityExists(mob), Is.False));
        await pair.CleanReturnAsync();
    }

    private static void MarkExpedition(IEntityManager entities, EntityUid map)
    {
        var expedition = entities.AddComponent<SalvageExpeditionComponent>(map);
        expedition.Station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
        expedition.MissionParams = new SalvageMissionParams { MissionType = SalvageMissionType.Elimination };
        expedition.Rewards = [];
        entities.System<SharedMapSystem>().SetPaused(entities.GetComponent<TransformComponent>(map).MapID, true);
    }
}
