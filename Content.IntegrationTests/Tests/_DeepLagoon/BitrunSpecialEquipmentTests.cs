using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Stacks;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Buckle.Components;
using Content.Shared.Item;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.StepTrigger.Systems;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunSpecialEquipmentTests
{
    [Test]
    public async Task MirageUsesItsThreeSecondFuseAndPointerLocksAfterTenUses()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            user = em.SpawnEntity("MobHuman", map.GridCoords);
            em.AddComponent<GodmodeComponent>(user);
            var grenade = em.SpawnEntity("BitrunSourceObjItemGrenadeMirage", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, grenade), Is.True);
            var prime = new UseInHandEvent(user);
            em.EventBus.RaiseLocalEvent(grenade, prime);
            Assert.That(prime.Handled, Is.True);
            Assert.That(em.GetComponent<BitrunMirageGrenadeComponent>(grenade).Thrower, Is.EqualTo(user));
            var target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.75f, 0)));
            var pointer = em.SpawnEntity("BitrunSourceObjItemLaserPointerBlue", map.GridCoords);
            for (var i = 0; i < 10; i++)
            {
                var point = new AfterInteractEvent(user, pointer, target, map.GridCoords, true);
                em.EventBus.RaiseLocalEvent(pointer, point);
                Assert.That(point.Handled, Is.True);
            }
            var battery = em.GetComponent<BitrunLaserPointerComponent>(pointer);
            Assert.That(battery.Energy, Is.Zero);
            Assert.That(battery.RechargeLocked, Is.True);
            var overuse = new AfterInteractEvent(user, pointer, target, map.GridCoords, true);
            em.EventBus.RaiseLocalEvent(pointer, overuse);
            Assert.That(battery.Energy, Is.Zero, "A depleted pointer cannot continue operating.");
            Assert.That(em.EntityQuery<BitrunMirageComponent>(), Is.Empty);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 4);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var mirage = em.EntityQuery<BitrunMirageComponent>().Single();
            Assert.That(em.GetComponent<MetaDataComponent>(mirage.Owner).EntityName,
                Is.EqualTo(em.GetComponent<MetaDataComponent>(user).EntityName));
            Assert.That(em.GetComponent<DamageableComponent>(mirage.Owner).TotalDamage.Float(), Is.Zero);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 16);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.EntityQuery<BitrunMirageComponent>(), Is.Empty));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CarverWaitsFiveSecondsAndConsumesTheGraspingRuneOnAnEnemy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid knife = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.AddComponent<GodmodeComponent>(user);
            em.AddComponent<BitrunHereticActorComponent>(user);
            knife = em.SpawnEntity("BitrunSourceObjItemMeleeRuneCarver", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, knife), Is.True);
            var select = new UseInHandEvent(user);
            em.EventBus.RaiseLocalEvent(knife, select);
            Assert.That(select.Handled, Is.True);
            var carve = new AfterInteractEvent(user, knife, null, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)), true);
            em.EventBus.RaiseLocalEvent(knife, carve);
            Assert.That(carve.Handled, Is.True);
            Assert.That(em.GetComponent<BitrunRuneCarverComponent>(knife).Runes, Is.Empty);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 4);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<BitrunRuneCarverComponent>(knife).Runes, Is.Empty));
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 2);
        EntityUid rune = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            rune = em.GetComponent<BitrunRuneCarverComponent>(knife).Runes.Single();
            Assert.That(em.GetComponent<MetaDataComponent>(rune).EntityPrototype!.ID, Is.EqualTo("BitrunGraspingCarving"));
            var friendly = new StepTriggeredOnEvent(rune, user);
            em.EventBus.RaiseLocalEvent(rune, ref friendly);
            Assert.That(em.GetComponent<BitrunCarvedRuneComponent>(rune).Charges, Is.EqualTo(1));
            var victim = em.SpawnEntity("MobHuman", map.GridCoords);
            var trigger = new StepTriggeredOnEvent(rune, victim);
            em.EventBus.RaiseLocalEvent(rune, ref trigger);
            Assert.That(em.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.GreaterThan(0));
            Assert.That(em.GetComponent<BitrunCarvedRuneComponent>(rune).Charges, Is.Zero);
        });
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.Deleted(rune), Is.True));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UtilityItemsStoreOneEvidenceAndDeployRealEntities()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var bag = em.SpawnEntity("BitrunSourceObjItemEvidencebag", map.GridCoords);
            var knife = em.SpawnEntity("CombatKnife", map.GridCoords);
            var second = em.SpawnEntity("CombatKnife", map.GridCoords);
            var slots = em.System<ItemSlotsSystem>();
            Assert.That(slots.TryInsert(bag, "evidence", knife, user), Is.True);
            Assert.That(slots.TryInsert(bag, "evidence", second, user), Is.False);
            Assert.That(em.GetComponent<ItemComponent>(bag).Size, Is.EqualTo(em.GetComponent<ItemComponent>(knife).Size));
            foreach (var (proto, result) in new[] {
                ("BitrunSourceObjItemEtherealballdeployer", "BitrunEtherealDiscoBall"),
                ("BitrunSourceObjItemMeleeSkateboardHoverboard", "BitrunRiddenHoverboard"),
                ("BitrunSourceObjItemStackMarkerBeaconTen", "BitrunPlacedMarkerBeacon") })
            {
                var item = em.SpawnEntity(proto, map.GridCoords);
                var use = new UseInHandEvent(user);
                em.EventBus.RaiseLocalEvent(item, use);
                Assert.That(use.Handled, Is.True, proto);
                var deployed = em.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == result).Owner;
                if (result == "BitrunRiddenHoverboard")
                    Assert.That(em.GetComponent<StrapComponent>(deployed).BuckledEntities, Does.Contain(user));
                if (em.TryGetComponent<StackComponent>(item, out var stack))
                    Assert.That(stack.Count, Is.EqualTo(9));
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PrototypeShoesConsumeBananiumOnActualTileSteps()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid wearer = default;
        EntityUid shoes = default;
        var before = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            maps.SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            wearer = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            shoes = entities.SpawnEntity("BitrunSourceObjItemClothingShoesClownShoesBananaShoes", map.GridCoords);
            var toggle = entities.System<ItemToggleSystem>();
            Assert.That(toggle.TryActivate(shoes, wearer), Is.False, "The source shoes start empty.");
            var material = entities.SpawnEntity("MaterialBananium1", map.GridCoords);
            var load = new InteractUsingEvent(wearer, material, shoes, map.GridCoords);
            entities.EventBus.RaiseLocalEvent(shoes, load);
            Assert.That(load.Handled, Is.True);
            Assert.That(entities.GetComponent<BitrunBananaShoesComponent>(shoes).Fuel, Is.EqualTo(10));
            Assert.That(entities.System<InventorySystem>().TryEquip(wearer, shoes, "shoes", silent: true), Is.True);
            Assert.That(toggle.TryActivate(shoes, wearer), Is.True);
            before = entities.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == "TrashBananaPeel");
            entities.System<SharedTransformSystem>().SetCoordinates(wearer, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
        });
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<BitrunBananaShoesComponent>(shoes).Fuel, Is.EqualTo(9));
            Assert.That(entities.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == "TrashBananaPeel"), Is.EqualTo(before + 1));
            entities.DeleteEntity(wearer);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HereticBladeRejectsAnOutsiderAndAcceptsTheDomainActor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var blade = entities.SpawnEntity("BitrunSourceObjItemMeleeSicklyBlade", map.GridCoords);
            var outsider = entities.SpawnEntity("MobHuman", map.GridCoords);
            var rejected = new AttemptMeleeEvent(outsider);
            entities.EventBus.RaiseLocalEvent(blade, ref rejected);
            Assert.That(rejected.Cancelled, Is.True);
            var actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.AddComponent<BitrunHereticActorComponent>(actor);
            var allowed = new AttemptMeleeEvent(actor);
            entities.EventBus.RaiseLocalEvent(blade, ref allowed);
            Assert.That(allowed.Cancelled, Is.False);
            entities.DeleteEntity(blade);
            entities.DeleteEntity(actor);
            entities.DeleteEntity(outsider);
        });
        await pair.CleanReturnAsync();
    }
}
