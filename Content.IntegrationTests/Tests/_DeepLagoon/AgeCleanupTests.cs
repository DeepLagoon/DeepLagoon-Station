using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Cleanup;
using Content.Server._DV.Mail.Components;
using Content.Server._Mono.Spawning;
using Content.Server._Mono.Cleanup;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Mind.Components;
using Content.Shared.Weapons.Misc;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class AgeCleanupTests
{
    [Test]
    public async Task SpaceLifetimeIsContinuousAndPickupOrGridRecoveryResetsIt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cleanup = entities.System<AgeCleanupSystem>();
            var transforms = entities.System<SharedTransformSystem>();
            var space = new EntityCoordinates(entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value, new Vector2(100, 100));
            var item = entities.SpawnEntity("SheetSteel1", space);
            Assert.That(cleanup.IsDue(item), Is.False);
            var age = entities.GetComponent<AgeCleanupComponent>(item);
            Assert.That(age.Due - age.Since, Is.EqualTo(TimeSpan.FromMinutes(15)));
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            age.Since = now - TimeSpan.FromSeconds(899);
            age.Due = age.Since + TimeSpan.FromMinutes(15);
            Assert.That(cleanup.IsDue(item), Is.False, "14:59 is still recoverable.");
            age.Due = now;
            Assert.That(cleanup.IsDue(item), Is.True, "15:00 meets the age threshold.");

            transforms.SetCoordinates(item, map.GridCoords);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(item), Is.False);
            Assert.That(cleanup.DeleteEligible(new[] { entities.GetNetEntity(item) }), Is.Zero);
            transforms.SetCoordinates(item, space);
            Assert.That(cleanup.IsDue(item), Is.False);
            Assert.That(entities.GetComponent<AgeCleanupComponent>(item).Due - now, Is.EqualTo(TimeSpan.FromMinutes(15)));

            var bag = entities.SpawnEntity("ClothingBackpack", space);
            var container = entities.System<SharedContainerSystem>().EnsureContainer<Container>(bag, "cleanup-test");
            Assert.That(entities.System<SharedContainerSystem>().Insert(item, container), Is.True);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(item), Is.False);
            Assert.That(cleanup.IsSpaceDebris(item), Is.False);
            Assert.That(cleanup.IsSpaceDebris(map.Grid), Is.False);
            var potentialPlayer = entities.SpawnEntity(null, space);
            entities.EnsureComponent<MindContainerComponent>(potentialPlayer);
            Assert.That(cleanup.IsSpaceDebris(potentialPlayer), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlayerTowCannotFallThroughToLegacySpaceCleanupAndDetachStartsFreshLifetime()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cleanup = entities.System<AgeCleanupSystem>();
            var legacy = entities.System<SpaceCleanupSystem>();
            var mapUid = entities.GetComponent<TransformComponent>(map.Grid).MapUid!.Value;
            var item = entities.SpawnEntity("SheetSteel1", new EntityCoordinates(mapUid, new Vector2(100000, 100000)));
            entities.EnsureComponent<ShipOwnershipComponent>(map.Grid);
            entities.EnsureComponent<TetheredComponent>(item).Tetherer = map.Grid;
            Assert.That(cleanup.IsSpaceDebris(item), Is.True, "Temporary recovery does not change debris classification.");
            Assert.That(legacy.ShouldCleanupSpaceEntity(item), Is.False,
                "Legacy distance/value cleanup must delegate to the same towing protection.");
            Assert.That(entities.HasComponent<AgeCleanupComponent>(item), Is.False,
                "An already recovered object does not start an abandonment timer.");
            Assert.That(cleanup.DeleteEligible(new[] { entities.GetNetEntity(item) }), Is.Zero);

            entities.RemoveComponent<TetheredComponent>(item);
            Assert.That(legacy.ShouldCleanupSpaceEntity(item), Is.False);
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            var age = entities.GetComponent<AgeCleanupComponent>(item);
            Assert.That(age.Since, Is.EqualTo(now));
            Assert.That(age.Due, Is.EqualTo(now + TimeSpan.FromMinutes(15)));
            age.Due = now;
            Assert.That(legacy.ShouldCleanupSpaceEntity(item), Is.True);
            entities.EnsureComponent<TetheredComponent>(item).Tetherer = map.Grid;
            Assert.That(legacy.ShouldCleanupSpaceEntity(item), Is.False,
                "Even overdue debris becomes protected when taken in tow.");
            Assert.That(entities.HasComponent<AgeCleanupComponent>(item), Is.False);
            Assert.That(cleanup.DeleteEligible(new[] { entities.GetNetEntity(item) }), Is.Zero);
            Assert.That(entities.EntityExists(item), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OpenedBuildABuddyWrapperExpiresWithoutDeletingItsGift()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        EntityUid mail = default, gift = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            mail = entities.SpawnEntity("MailNFBuildABuddy", map.GridCoords);
            var user = entities.SpawnEntity("MobHuman", map.GridCoords);
            gift = entities.SpawnEntity("Screwdriver", map.GridCoords);
            var containers = entities.System<SharedContainerSystem>();
            var contents = containers.EnsureContainer<Container>(mail, "contents");
            Assert.That(containers.Insert(gift, contents), Is.True);
            entities.GetComponent<MailComponent>(mail).IsLocked = false;
            entities.EventBus.RaiseLocalEvent(mail, new UseInHandEvent(user));
            Assert.That(entities.GetComponent<MailComponent>(mail).IsEnabled, Is.False);
            Assert.That(contents.ContainedEntities, Is.Empty);
            var age = entities.GetComponent<AgeCleanupComponent>(mail);
            Assert.That(age.Kind, Is.EqualTo(AgeCleanupKind.OpenedMail));
            Assert.That(age.Due - age.Since, Is.EqualTo(TimeSpan.FromMinutes(5)));
            age.Due = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            Assert.That(entities.System<AgeCleanupSystem>().DeleteEligible(new[] { entities.GetNetEntity(mail) }), Is.EqualTo(1));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.EntityExists(mail), Is.False);
            Assert.That(pair.Server.EntMan.EntityExists(gift), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DroneNeedsLastCoreDestroyedAndRecoveryCancelsDeletion()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cleanup = entities.System<AgeCleanupSystem>();
            var first = entities.SpawnEntity("NpcDroneAiMedusa", map.GridCoords);
            var second = entities.SpawnEntity("NpcDroneAiAssembly", map.GridCoords);
            entities.EnsureComponent<NpcDroneCleanupComponent>(map.Grid);
            var legacyState = entities.EnsureComponent<GridCleanupGridComponent>(map.Grid);
            legacyState.CleanupAccumulator = TimeSpan.MaxValue;
            legacyState.IgnoreIFF = legacyState.IgnorePowered = legacyState.IgnorePrice = true;
            legacyState.DistanceOverride = 0;
            var gridCleanup = entities.System<GridCleanupSystem>();
            Assert.That(gridCleanup.ShouldCleanupGrid(map.Grid), Is.True,
                "Living distant NPCs keep the existing distance-based cleanup policy.");
            Assert.That(cleanup.IsDue(map.Grid), Is.False);
            entities.DeleteEntity(first);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(map.Grid), Is.False);
            entities.DeleteEntity(second);
            var age = entities.GetComponent<AgeCleanupComponent>(map.Grid);
            Assert.That(age.Kind, Is.EqualTo(AgeCleanupKind.DefeatedDrone));
            Assert.That(age.Due - age.Since, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(cleanup.IsDue(map.Grid), Is.False);
            Assert.That(gridCleanup.ShouldCleanupGrid(map.Grid), Is.False,
                "A defeated wreck cannot bypass its fifteen-minute lifetime through legacy grid cleanup.");
            age.Due = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            Assert.That(cleanup.IsDue(map.Grid), Is.True);
            var repaired = entities.SpawnEntity("NpcDroneAiMedusa", map.GridCoords);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(map.Grid), Is.False);
            Assert.That(gridCleanup.ShouldCleanupGrid(map.Grid), Is.True,
                "Reviving the drone restores its original living-NPC cleanup policy.");
            entities.DeleteEntity(repaired);
            entities.GetComponent<AgeCleanupComponent>(map.Grid).Due = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            entities.EnsureComponent<ShipOwnershipComponent>(map.Grid);
            Assert.That(cleanup.IsDue(map.Grid), Is.False);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(map.Grid), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ActualDroneSpawnerInheritsExplicitCleanupMarker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.Index<EntityPrototype>("SpawnDroneMote")
                .TryGetComponent<GridSpawnerComponent>(out var spawner, pair.Server.EntMan.ComponentFactory), Is.True);
            Assert.That(spawner!.AddComponents.Keys, Does.Contain("NpcDroneCleanup"));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlayerTowCancelsDefeatedWreckTimerAndAbandonmentStartsFreshLifetime()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var cleanup = entities.System<AgeCleanupSystem>();
            var core = entities.SpawnEntity("NpcDroneAiMedusa", map.GridCoords);
            entities.EnsureComponent<NpcDroneCleanupComponent>(map.Grid);
            entities.DeleteEntity(core);
            var now = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            entities.GetComponent<AgeCleanupComponent>(map.Grid).Due = now;
            var parent = entities.GetComponent<TransformComponent>(map.Grid);
            var tug = entities.System<SharedMapSystem>().CreateGridEntity(parent.MapID);
            entities.EnsureComponent<ShipOwnershipComponent>(tug);
            entities.EnsureComponent<TetheredComponent>(map.Grid).Tetherer = tug;
            Assert.That(cleanup.IsDue(map.Grid), Is.False);
            Assert.That(entities.HasComponent<AgeCleanupComponent>(map.Grid), Is.False);
            entities.RemoveComponent<TetheredComponent>(map.Grid);
            Assert.That(cleanup.IsDue(map.Grid), Is.False);
            Assert.That(entities.GetComponent<AgeCleanupComponent>(map.Grid).Due - now, Is.EqualTo(TimeSpan.FromMinutes(15)));
        });
        await pair.CleanReturnAsync();
    }
}
