using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._Mono.Cleanup;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentGearTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(false)]
    [TestCase(true)]
    public async Task QueuedCleanupCannotDestroySleepingVisitorsGearOrMixOwners(bool ownerLeavesFirst)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        foreach (var session in sessions)
            await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame, 600);
        var bodies = new EntityUid[2];
        var uniforms = new EntityUid[2];
        var ids = new EntityUid[2];
        var bags = new EntityUid[2];
        var contents = new EntityUid[2];
        var held = new EntityUid[2];
        var escrows = new EntityUid[2];
        var hands = new string[2];
        EntityUid looseControl = default;

        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var apartments = e.System<ApartmentSystem>();
            var inventory = e.System<InventorySystem>();
            var containers = e.System<SharedContainerSystem>();
            var cleanup = e.System<SpaceCleanupSystem>();
            var queue = (Queue<EntityUid>) typeof(BaseCleanupSystem<PhysicsComponent>)
                .GetField("_checkQueue", Private)!.GetValue(cleanup)!;
            queue.Clear();
            // Hold the pre-entry queue until after both disconnect/reconnect events.
            typeof(BaseCleanupSystem<PhysicsComponent>).GetField("_cleanupDeferDuration", Private)!.SetValue(cleanup, TimeSpan.FromDays(1));

            for (var i = 0; i < 2; i++)
            {
                bodies[i] = e.SpawnEntity("MobHuman", map.GridCoords);
                pair.Server.PlayerMan.SetAttachedEntity(sessions[i], bodies[i]);
                uniforms[i] = e.SpawnEntity(i == 0 ? "ClothingUniformJumpsuitColorBlue" : "ClothingUniformJumpsuitColorRed", map.GridCoords);
                ids[i] = e.SpawnEntity("PassengerIDCard", map.GridCoords);
                bags[i] = e.SpawnEntity("ClothingBackpack", map.GridCoords);
                contents[i] = e.SpawnEntity("Pen", map.GridCoords);
                held[i] = e.SpawnEntity("Crowbar", map.GridCoords);
                Assert.That(inventory.TryEquip(bodies[i], uniforms[i], "jumpsuit", force: true), Is.True);
                Assert.That(inventory.TryEquip(bodies[i], ids[i], "id", force: true), Is.True);
                Assert.That(inventory.TryEquip(bodies[i], bags[i], "back", force: true), Is.True);
                Assert.That(containers.Insert(contents[i], e.GetComponent<StorageComponent>(bags[i]).Container), Is.True);
                Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(bodies[i], held[i]), Is.True);
                hands[i] = e.GetComponent<HandsComponent>(bodies[i]).Hands.Single(h => h.Value.HeldEntity == held[i]).Key;
                // A cleanup pass can queue an equipped item before apartment entry.
                foreach (var item in new[] { uniforms[i], ids[i], bags[i], contents[i], held[i] }) queue.Enqueue(item);
            }

            for (var i = 0; i < 2; i++)
                Assert.That(apartments.Enter(sessions[i], sessions[0].UserId, out var message), Is.True, message);
        });
        // End and recreate sessions just as a real reconnection does. Reusing a
        // disconnected session in the same tick breaks playtime tracker invariants.
        for (var i = 0; i < 2; i++)
            await pair.Server.RemoveDummySession(sessions[i]);
        await pair.Server.WaitAssertion(() =>
        {
            var apartments = pair.Server.EntMan.System<ApartmentSystem>();
            var home = apartments.Instances[sessions[0].UserId];
            apartments.RefreshActivity(home);
            Assert.That(home.Frozen, Is.True);
        });

        // Only the guest returns in the morning, leaving the owner asleep.
        sessions[1] = await pair.Server.AddDummySession("integration_dummy_1");
        await PoolManager.WaitUntil(pair.Server, () => sessions[1].Status == SessionStatus.InGame, 600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var apartments = e.System<ApartmentSystem>();
            var containers = e.System<SharedContainerSystem>();
            var cleanup = e.System<SpaceCleanupSystem>();
            var queue = (Queue<EntityUid>) typeof(BaseCleanupSystem<PhysicsComponent>)
                .GetField("_checkQueue", Private)!.GetValue(cleanup)!;
            var home = apartments.Instances[sessions[0].UserId];
            pair.Server.PlayerMan.SetAttachedEntity(sessions[1], bodies[1]);
            apartments.RefreshActivity(home);
            Assert.That(home.Frozen, Is.False);

            var shouldClean = typeof(SpaceCleanupSystem).GetMethod("ShouldEntityCleanup", Private, [typeof(EntityUid)])!;
            for (var i = 0; i < 2; i++)
            {
                foreach (var item in new[] { uniforms[i], ids[i], bags[i], contents[i], held[i] })
                {
                    Assert.That(e.GetComponent<MetaDataComponent>(item).EntityPaused, Is.True,
                        $"Deposited item {item} must remain paused when the apartment wakes.");
                    Assert.That(shouldClean.Invoke(cleanup, [item]), Is.False, "Deposited gear must survive a pre-existing cleanup queue.");
                }
                Assert.That(containers.TryGetContainingContainer(uniforms[i], out var storage), Is.True);
                escrows[i] = storage.Owner;
            }
            Assert.That(escrows[0], Is.Not.EqualTo(escrows[1]), "Each player needs independent escrow.");
            // Reproduce the former storage location and prove cleanup is active.
            var sleepMap = (EntityUid) typeof(ApartmentSystem).GetField("_sleepMap", Private)!.GetValue(apartments)!;
            looseControl = e.SpawnEntity("ClothingUniformJumpsuitColorBlue", new EntityCoordinates(sleepMap, 0, 0));
            Assert.That(shouldClean.Invoke(cleanup, [looseControl]), Is.True, "Loose gear on the sleep map was eligible for deletion.");
            queue.Enqueue(looseControl);
            typeof(BaseCleanupSystem<PhysicsComponent>).GetField("_cleanupDeferDuration", Private)!.SetValue(cleanup, TimeSpan.Zero);
            cleanup.Update(.1f);
        });
        await pair.RunTicksSync(5);
        foreach (var i in ownerLeavesFirst ? new[] { 0, 1 } : new[] { 1, 0 })
        {
            if (i == 0)
            {
                sessions[0] = await pair.Server.AddDummySession("integration_dummy_0");
                await PoolManager.WaitUntil(pair.Server, () => sessions[0].Status == SessionStatus.InGame, 600);
            }
            await pair.Server.WaitAssertion(() =>
            {
                var e = pair.Server.EntMan;
                var apartments = e.System<ApartmentSystem>();
                var inventory = e.System<InventorySystem>();
                Assert.That(e.EntityExists(looseControl), Is.False, "The queued cleanup actually ran.");
                pair.Server.PlayerMan.SetAttachedEntity(sessions[i], bodies[i]);
                Assert.That(apartments.Leave(sessions[i].UserId, out var message), Is.True, message);
                foreach (var (slot, expected) in new[] { ("jumpsuit", uniforms[i]), ("id", ids[i]), ("back", bags[i]) })
                {
                    Assert.That(inventory.TryGetSlotEntity(bodies[i], slot, out var restored), Is.True);
                    Assert.That(restored, Is.EqualTo(expected), "Return the same player's original entity.");
                    Assert.That(e.GetComponent<MetaDataComponent>(expected).EntityPaused, Is.False);
                }
                Assert.That(e.GetComponent<StorageComponent>(bags[i]).Container.Contains(contents[i]), Is.True);
                Assert.That(e.GetComponent<HandsComponent>(bodies[i]).Hands[hands[i]].HeldEntity, Is.EqualTo(held[i]));
                Assert.That(e.EntityExists(escrows[i]), Is.False, "Empty escrow is removed after restoring its contents.");
                Assert.That(e.GetComponent<TransformComponent>(bodies[i]).GridUid, Is.EqualTo(map.Grid.Owner));
                Assert.That(apartments.Leave(sessions[i].UserId, out _), Is.False, "A second exit must not duplicate items.");
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            for (var i = 0; i < 2; i++)
            {
                pair.Server.PlayerMan.SetAttachedEntity(sessions[i], null);
                e.DeleteEntity(bodies[i]);
            }
        });
        await pair.CleanReturnAsync();
    }
}
