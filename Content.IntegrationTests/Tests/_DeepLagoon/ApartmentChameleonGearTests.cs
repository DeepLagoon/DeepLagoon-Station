using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ApartmentChameleonGearTests
{
    private static readonly (string Slot, string Prototype)[] Equipped =
    [
        ("jumpsuit", "ClothingUniformJumpsuitChameleon"),
        ("shoes", "ClothingShoesChameleon"),
        ("ears", "ClothingHeadsetChameleon"),
        ("id", "ChameleonPDA"),
        ("gloves", "ClothingHandsChameleon"),
        ("back", "ClothingBackpackChameleonFill"),
    ];

    [Test]
    public async Task EntryEquipsUplinkKitAndExitRemovesDroppedAndTransferredGear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var sessions = await pair.Server.AddDummySessions(2);
        foreach (var session in sessions)
            await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame, 600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var apartments = e.System<ApartmentSystem>();
            var inventory = e.System<InventorySystem>();
            var containers = e.System<SharedContainerSystem>();
            var bodies = sessions.Select(session =>
            {
                var body = e.SpawnEntity("MobHuman", map.GridCoords);
                pair.Server.PlayerMan.SetAttachedEntity(session, body);
                return body;
            }).ToArray();
            var original = e.SpawnEntity("ClothingUniformJumpsuitColorBlue", map.GridCoords);
            Assert.That(inventory.TryEquip(bodies[0], original, "jumpsuit", force: true), Is.True);
            foreach (var session in sessions)
                Assert.That(apartments.Enter(session, sessions[0].UserId, out var message), Is.True, message);
            var home = apartments.Instances[sessions[0].UserId];
            apartments.Update(0); // Normal equipment changes must not expire the kit.
            var kit = e.EntityQuery<ApartmentChameleonGearComponent>(true)
                .Select(comp => comp.Owner).ToArray();
            EntityUid backpack = default;
            foreach (var body in bodies)
            foreach (var (slot, prototype) in Equipped)
            {
                Assert.That(inventory.TryGetSlotEntity(body, slot, out var item), Is.True, slot);
                Assert.That(e.GetComponent<MetaDataComponent>(item.Value).EntityPrototype?.ID, Is.EqualTo(prototype));
                if (body == bodies[0] && slot == "back") backpack = item.Value;
            }
            var storage = e.GetComponent<StorageComponent>(backpack).Container;
            Assert.That(storage.ContainedEntities.Select(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID),
                Is.EquivalentTo(new[] { "ClothingOuterChameleon", "ClothingNeckChameleon", "ClothingMaskGasChameleon",
                    "ClothingHeadHatChameleon", "ClothingEyesChameleon", "BarberScissors" }));
            var firstKit = Descendants(e, backpack).Append(backpack).ToList();
            foreach (var (slot, _) in Equipped.Where(p => p.Slot != "back"))
            {
                inventory.TryGetSlotEntity(bodies[0], slot, out var item);
                firstKit.Add(item.Value);
                firstKit.AddRange(Descendants(e, item.Value));
            }
            var hat = storage.ContainedEntities.Single(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "ClothingHeadHatChameleon");
            Assert.That(containers.Remove(hat, storage, destination: new EntityCoordinates(home.Grid, 2.5f, 2.5f)), Is.True);
            var eyes = storage.ContainedEntities.Single(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "ClothingEyesChameleon");
            inventory.TryGetSlotEntity(bodies[1], "back", out var guestBag);
            Assert.That(containers.Insert(eyes, e.GetComponent<StorageComponent>(guestBag.Value).Container), Is.True);
            // A non-kit box and its contents must survive without spilling separately.
            var purchased = e.SpawnEntity("BoxCardboard", new EntityCoordinates(home.Grid, 2.5f, 2.5f));
            var pen = e.SpawnEntity("Pen", e.GetComponent<TransformComponent>(purchased).Coordinates);
            Assert.That(containers.Insert(pen, e.GetComponent<StorageComponent>(purchased).Container), Is.True);
            // Exercise cleanup independently of the storage capacity checks.
            var pocket = containers.EnsureContainer<Container>(backpack, "test-non-kit-pocket");
            Assert.That(containers.Insert(purchased, pocket), Is.True);
            var snapshot = ((string Current, string RoundEnd)) typeof(ApartmentSystem)
                .GetMethod("SerializeApartment", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(apartments, [home])!;
            Assert.That(snapshot.Current, Does.Not.Contain("ClothingHeadHatChameleon").And.Not.Contain("ClothingEyesChameleon"),
                "Temporary gear on the floor or in another visitor's bag must not persist.");
            Assert.That(apartments.Leave(sessions[0].UserId, out var leave), Is.True, leave);
            Assert.That(firstKit.All(item => !e.EntityExists(item)), Is.True);
            Assert.That(e.EntityExists(guestBag.Value), Is.True);
            Assert.That(e.GetComponent<TransformComponent>(purchased).GridUid, Is.EqualTo(home.Grid));
            Assert.That(e.GetComponent<StorageComponent>(purchased).Container.Contains(pen), Is.True);
            Assert.That(inventory.TryGetSlotEntity(bodies[0], "jumpsuit", out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(original));
            Assert.That(apartments.Leave(sessions[1].UserId, out leave), Is.True, leave);
            Assert.That(kit.All(item => !e.EntityExists(item)), Is.True);
            foreach (var (session, body) in sessions.Zip(bodies))
            {
                pair.Server.PlayerMan.SetAttachedEntity(session, null);
                e.DeleteEntity(body);
            }
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task GearCannotEscapeInsideBodyOrNestedContainer(int transport)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        var session = await pair.Server.AddDummySession();
        await PoolManager.WaitUntil(pair.Server, () => session.Status == SessionStatus.InGame, 600);
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(ApartmentCVars.Enabled, true);
            config.SetCVar(ApartmentCVars.DefaultTemplate, "DLApartmentSmall");
            var apartments = e.System<ApartmentSystem>();
            var inventory = e.System<InventorySystem>();
            var containers = e.System<SharedContainerSystem>();
            var transform = e.System<SharedTransformSystem>();
            var body = e.SpawnEntity("MobHuman", map.GridCoords);
            pair.Server.PlayerMan.SetAttachedEntity(session, body);
            Assert.That(apartments.Enter(session, session.UserId, out var message), Is.True, message);
            var home = apartments.Instances[session.UserId];
            inventory.TryGetSlotEntity(body, "back", out var backpack);
            var kit = Descendants(e, backpack.Value).Append(backpack.Value).ToArray();
            EntityUid mover = body;
            if (transport == 1)
            {
                var crate = e.SpawnEntity("ClothingBackpack", new EntityCoordinates(home.Grid, 2.5f, 2.5f));
                Assert.That(inventory.TryUnequip(body, "back", out _, force: true), Is.True);
                Assert.That(containers.Insert(backpack.Value, e.GetComponent<StorageComponent>(crate).Container), Is.True);
                mover = crate;
            }
            if (transport == 2)
            {
                var storage = e.GetComponent<StorageComponent>(backpack.Value).Container;
                var hat = storage.ContainedEntities.Single(item => e.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "ClothingHeadHatChameleon");
                Assert.That(containers.Remove(hat, storage), Is.True);
                mover = hat;
                kit = [hat];
            }
            transform.SetCoordinates(mover, map.GridCoords);
            apartments.Update(0);
            Assert.That(kit.All(item => !e.EntityExists(item)), Is.True, "Crossing the apartment grid removes every nested kit item.");
            Assert.That(apartments.Leave(session.UserId, out message), Is.True, message);
            pair.Server.PlayerMan.SetAttachedEntity(session, null);
            e.DeleteEntity(body);
            if (transport == 1) e.DeleteEntity(mover);
        });
        await pair.CleanReturnAsync();
    }

    private static System.Collections.Generic.IEnumerable<EntityUid> Descendants(IEntityManager entities, EntityUid parent)
    {
        var children = entities.GetComponent<TransformComponent>(parent).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            yield return child;
            foreach (var descendant in Descendants(entities, child)) yield return descendant;
        }
    }
}
