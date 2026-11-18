using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentDeletedBodyTests
{
    [Test]
    public async Task ChangingControlledBodyDoesNotReleaseExistingResidentOrGiveAwayEscrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var session = await pair.Server.AddDummySession();
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        EntityUid resident = default, otherBody = default, uniform = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(ApartmentCVars.Enabled, true);
            resident = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, resident);
            uniform = e.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(resident, uniform, "jumpsuit", force: true), Is.True);
            Assert.That(e.System<ApartmentSystem>().Enter(session, session.UserId, out var message), Is.True, message);
            otherBody = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, otherBody);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var apartments = e.System<ApartmentSystem>();
            RunMaintenance(apartments);
            Assert.That(apartments.TryGetVisit(session.UserId, out _, out var body), Is.True);
            Assert.That(body, Is.EqualTo(resident));
            Assert.That(apartments.Enter(session, session.UserId, out _), Is.False);
            Assert.That(e.System<SharedContainerSystem>().TryGetContainingContainer(uniform, out var storage), Is.True);
            Assert.That(storage.ID, Is.EqualTo("apartment-gear"));
            pair.Server.PlayerMan.SetAttachedEntity(session, resident);
            Assert.That(apartments.Leave(session.UserId, out var message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(resident);
            e.DeleteEntity(otherBody);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeletedBodyDoesNotBlockImmediateOrDelayedReentryAndKeepsExactGear(bool runMaintenance)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var session = await pair.Server.AddDummySession();
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        EntityUid replacement = default, uniform = default, bag = default, pen = default, escrow = default;

        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            var apartments = e.System<ApartmentSystem>();
            var inventory = e.System<InventorySystem>();
            var containers = e.System<SharedContainerSystem>();
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            uniform = e.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            bag = e.SpawnEntity("ClothingBackpack", map.GridCoords);
            pen = e.SpawnEntity("Pen", map.GridCoords);
            Assert.That(inventory.TryEquip(body, uniform, "jumpsuit", force: true), Is.True);
            Assert.That(inventory.TryEquip(body, bag, "back", force: true), Is.True);
            Assert.That(containers.Insert(pen, e.GetComponent<StorageComponent>(bag).Container), Is.True);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            Assert.That(containers.TryGetContainingContainer(uniform, out var storage), Is.True);
            escrow = storage.Owner;
            var home = apartments.Instances[session.UserId];
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(body);
            Assert.That(apartments.TryGetVisit(session.UserId, out _, out _), Is.False,
                "The elevator must not reject a replacement body because of a deleted visitor.");
            Assert.That(apartments.Visitors(home), Is.Empty);
            replacement = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, replacement);

            if (!runMaintenance)
                Assert.That(apartments.Enter(session, session.UserId, out message), Is.True, message);
        });

        if (runMaintenance)
        {
            await pair.RunTicksSync(2);
            await pair.Server.WaitAssertion(() =>
            {
                var e = pair.Server.EntMan;
                var apartments = e.System<ApartmentSystem>();
                RunMaintenance(apartments);
                var inventory = e.System<InventorySystem>();
                Assert.That(inventory.TryGetSlotEntity(replacement, "jumpsuit", out var returned), Is.True);
                Assert.That(returned, Is.EqualTo(uniform));
                Assert.That(e.EntityExists(escrow), Is.False);
                Assert.That(e.GetComponent<TransformComponent>(replacement).GridUid, Is.EqualTo(map.Grid.Owner),
                    "Recovery must not teleport the replacement to the deleted body's apartment.");
                Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            });
        }

        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var apartments = e.System<ApartmentSystem>();
            Assert.That(e.EntityExists(escrow), Is.False);
            Assert.That(apartments.TryGetVisit(session.UserId, out _, out var resident), Is.True);
            Assert.That(resident, Is.EqualTo(replacement));
            Assert.That(apartments.Leave(session.UserId, out var message), Is.True, message);
            var inventory = e.System<InventorySystem>();
            Assert.That(inventory.TryGetSlotEntity(replacement, "jumpsuit", out var returned), Is.True);
            Assert.That(returned, Is.EqualTo(uniform));
            Assert.That(inventory.TryGetSlotEntity(replacement, "back", out returned), Is.True);
            Assert.That(returned, Is.EqualTo(bag));
            Assert.That(e.GetComponent<StorageComponent>(bag).Container.Contains(pen), Is.True);
            Assert.That(apartments.Leave(session.UserId, out _), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(replacement);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DeletedDisconnectedVisitorReleasesApartmentAndKeepsEscrowUntilLivingReconnect()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var session = await pair.Server.AddDummySession("deleted_apartment_visitor");
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        var user = session.UserId;
        EntityUid body = default, uniform = default, escrow = default, ghost = default, replacement = default;

        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.EmptyTimeout, 1);
            var apartments = e.System<ApartmentSystem>();
            body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            uniform = e.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(body, uniform, "jumpsuit", force: true), Is.True);
            Assert.That(apartments.Enter(session, user, out var message), Is.True, message);
            Assert.That(e.System<SharedContainerSystem>().TryGetContainingContainer(uniform, out var storage), Is.True);
            escrow = storage.Owner;
        });
        await pair.Server.RemoveDummySession(session);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            e.DeleteEntity(body);
            var apartments = e.System<ApartmentSystem>();
            RunMaintenance(apartments);
            apartments.Instances[user].IdleSince = TimeSpan.FromSeconds(-10000);
            RunMaintenance(apartments);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var apartments = e.System<ApartmentSystem>();
            Assert.That(apartments.Instances.ContainsKey(user), Is.False, "A deleted visitor must not prevent unloading.");
            Assert.That(e.EntityExists(escrow), Is.True);
            Assert.That(e.EntityExists(uniform), Is.True);
        });

        session = await pair.Server.AddDummySession("deleted_apartment_visitor");
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(session.UserId, Is.EqualTo(user));
            ghost = pair.Server.EntMan.SpawnEntity("MobObserver", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, ghost);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            RunMaintenance(e.System<ApartmentSystem>());
            Assert.That(e.EntityExists(escrow), Is.True, "A ghost cannot collect escrow.");
            replacement = e.SpawnEntity("MobHuman", map.GridCoords);
            var occupiedSlot = e.SpawnEntity("ClothingUniformJumpsuitColorRed", map.GridCoords);
            Assert.That(e.System<InventorySystem>().TryEquip(replacement, occupiedSlot, "jumpsuit", force: true), Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(session, replacement);
            e.DeleteEntity(ghost);
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            RunMaintenance(e.System<ApartmentSystem>());
            Assert.That(e.EntityExists(escrow), Is.False);
            Assert.That(e.EntityExists(uniform), Is.True, "An occupied slot must not destroy returned gear.");
            Assert.That(e.GetComponent<TransformComponent>(uniform).Coordinates,
                Is.EqualTo(e.GetComponent<TransformComponent>(replacement).Coordinates));
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(replacement);
        });
        await pair.CleanReturnAsync();
    }

    private static void RunMaintenance(ApartmentSystem apartments)
    {
        // Apartment housekeeping follows wall time rather than simulated game ticks.
        typeof(ApartmentSystem).GetField("_nextUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(apartments, TimeSpan.Zero);
        apartments.Update(0);
    }
}
