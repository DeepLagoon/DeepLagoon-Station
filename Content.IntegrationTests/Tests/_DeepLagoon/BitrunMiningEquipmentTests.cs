using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Server.NPC.Systems;
using Content.Server.Power.Components;
using Content.Shared.CCVar;
using Content.Shared.CombatMode;
using Content.Shared.Damage.Components;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Marker;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Components;
using Content.Shared.Weapons.Melee.Events;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Content.Shared.Inventory;
using Content.Shared.Actions.Events;
using Content.Server.Mind;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunMiningEquipmentTests
{
    [Test]
    public async Task HunterMittsTeachHooksComboCritAndKeepTrainingOnTheMind()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            Floor(pair, map);
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            var minds = em.System<MindSystem>();
            var mind = minds.CreateMind(null);
            minds.TransferTo(mind, user);
            var target = em.SpawnEntity("BitrunMobColossus", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            var mitts = em.SpawnEntity("BitrunSourceObjItemClothingGlovesFingerlessPunchMitts", map.GridCoords);
            var inventory = em.System<InventorySystem>();
            Assert.That(inventory.TryEquip(user, mitts, "gloves", silent: true), Is.True);
            Assert.That(em.HasComponent<BitrunHunterBoxerComponent>(user), Is.True);
            var system = em.System<BitrunHunterBoxingSystem>();
            var hit = new MeleeHitEvent(new() { target }, user, mitts, new(), null);
            em.EventBus.RaiseLocalEvent(mitts, hit);
            Assert.That(hit.Handled, Is.True);
            Assert.That(em.GetComponent<BitrunHunterBoxerComponent>(user).PreviousHook, Is.EqualTo('H'));
            var before = em.GetComponent<DamageableComponent>(target).TotalDamage.Float();
            var disarm = new DisarmAttemptEvent(target, user);
            em.EventBus.RaiseLocalEvent(target, disarm);
            Assert.That(disarm.Cancelled, Is.True);
            Assert.That(em.GetComponent<BitrunHunterBoxerComponent>(user).PreviousHook, Is.EqualTo(default(char)));
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float() - before, Is.GreaterThanOrEqualTo(20));
            Assert.That(em.GetComponent<BitrunBoxingTrainingComponent>(mind).Experience, Is.Positive);
            em.System<DamageableSystem>().TryChangeDamage(user, new DamageSpecifier { DamageDict = { ["Blunt"] = 30, ["Heat"] = 30, ["Poison"] = 30 } }, true);
            before = em.GetComponent<DamageableComponent>(user).TotalDamage.Float();
            system.ApplyCritical(user, target, 12);
            Assert.That(em.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.EqualTo(before - 12).Within(0.01f));
            Assert.That(em.HasComponent<BitrunRebukedComponent>(target), Is.True);
            var rate = new GetMeleeAttackRateEvent(target, 1, 1, target);
            em.EventBus.RaiseLocalEvent(target, ref rate);
            Assert.That(rate.Multipliers, Is.EqualTo(0.5f));
            var experience = em.GetComponent<BitrunBoxingTrainingComponent>(mind).Experience;
            Assert.That(inventory.TryUnequip(user, "gloves", silent: true), Is.True);
            Assert.That(system.Punch(user, target, 'H'), Is.False);
            Assert.That(em.GetComponent<BitrunBoxingTrainingComponent>(mind).Experience, Is.EqualTo(experience));
            Assert.That(BitrunHunterBoxingSystem.Level(2500), Is.EqualTo(7));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CrusherFiresZeroDamageMarkThenDetonatesOnceWithSourceBackstabDamage()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default, crusher = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(pair, map);
            var em = pair.Server.EntMan;
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.AddComponent<GodmodeComponent>(user);
            target = em.SpawnEntity("BitrunMobColossus", map.GridCoords.Offset(new Vector2(3.5f, 0.5f)));
            crusher = em.SpawnEntity("BitrunSourceObjItemKineticCrusher", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickup(user, crusher), Is.True);
            var attempt = new AttemptMeleeEvent(user);
            em.EventBus.RaiseLocalEvent(crusher, ref attempt);
            Assert.That(attempt.Cancelled, Is.True);
            Assert.That(em.System<SharedWieldableSystem>().TryWield(crusher, em.GetComponent<WieldableComponent>(crusher), user), Is.True);
            em.System<SharedCombatModeSystem>().SetInCombatMode(user, true);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            em.System<GunSystem>().AttemptShoot(user, crusher, em.GetComponent<GunComponent>(crusher), em.GetComponent<TransformComponent>(target).Coordinates);
            Assert.That(em.GetComponent<BasicEntityAmmoProviderComponent>(crusher).Count, Is.Zero,
                $"The wielded crusher actually fires a bolt. wield={em.GetComponent<WieldableComponent>(crusher).Wielded}; rate={em.GetComponent<GunComponent>(crusher).FireRateModified}; fire={em.GetComponent<GunComponent>(crusher).NextFire}; melee={em.GetComponent<MeleeWeaponComponent>(crusher).NextAttack}; now={pair.Server.Timing.CurTime}");
            Assert.That(em.GetComponent<RechargeBasicEntityAmmoComponent>(crusher).RechargeCooldown, Is.EqualTo(1.5f));
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.HasComponent<DamageMarkerComponent>(target), Is.True);
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Zero);
            var transform = em.System<SharedTransformSystem>();
            // Put the attacker behind the target regardless of its initial facing.
            var behind = transform.GetWorldRotation(target).ToWorldVec() * -1;
            transform.SetCoordinates(user, em.GetComponent<TransformComponent>(target).Coordinates.Offset(behind));
            em.System<SharedCombatModeSystem>().SetInCombatMode(user, true);
            var weapon = em.GetComponent<MeleeWeaponComponent>(crusher);
            Assert.That(em.System<SharedMeleeWeaponSystem>().AttemptLightAttack(user, crusher, weapon, target), Is.True);
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.EqualTo(100).Within(0.01f));
            Assert.That(em.HasComponent<DamageMarkerComponent>(target), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    private static void Floor(TestPair pair, TestMapData map)
    {
        var maps = pair.Server.EntMan.System<SharedMapSystem>();
        for (var x = -6; x <= 12; x++)
        for (var y = -6; y <= 6; y++)
            maps.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);
    }

    [Test]
    public async Task ResonatorWaitsTwoSecondsHurtsAndMinesWithSourceFieldLimits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid tool = default, target = default, rock = default, user = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(pair, map);
            var em = pair.Server.EntMan;
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            em.RemoveComponent<RespiratorComponent>(target);
            em.RemoveComponent<BarotraumaComponent>(target);
            em.RemoveComponent<TemperatureComponent>(target);
            tool = em.SpawnEntity("BitrunSourceObjItemResonator", map.GridCoords);
            rock = em.SpawnEntity("AsteroidRockGold", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            Assert.That(em.System<BitrunResonatorSystem>().CreateField((tool, em.GetComponent<BitrunResonatorComponent>(tool)), em.GetComponent<TransformComponent>(target).Coordinates, user), Is.True);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.Zero));
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 4);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.GreaterThanOrEqualTo(60));
            Assert.That(em.Deleted(rock), Is.True, "The adjacent rock receives a propagated field and yields ore.");
            Assert.That(em.AllEntityQueryEnumerator<BitrunResonanceSlowComponent>().MoveNext(out _, out _), Is.True);
            em.EventBus.RaiseLocalEvent(tool, new UseInHandEvent(user));
            var resonator = em.GetComponent<BitrunResonatorComponent>(tool);
            var system = em.System<BitrunResonatorSystem>();
            for (var i = 0; i < 4; i++)
                Assert.That(system.CreateField((tool, resonator), map.GridCoords.Offset(new Vector2(i * 2 + 0.5f, 3.5f)), user), Is.True);
            Assert.That(system.CreateField((tool, resonator), map.GridCoords.Offset(new Vector2(10.5f, 3.5f)), user), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MinebotCollectsDumpsAndFiresItsRealAccelerator()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var config = pair.Server.ResolveDependency<IConfigurationManager>();
        var enabled = config.GetCVar(CCVars.NPCEnabled);
        var paused = config.GetCVar(CCVars.NPCPauseWhenNoPlayersInRange);
        EntityUid bot = default, ore = default, victim = default;
        try
        {
            await pair.Server.WaitAssertion(() =>
            {
                Floor(pair, map);
                var em = pair.Server.EntMan;
                config.SetCVar(CCVars.NPCEnabled, true);
                config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false);
                em.System<NPCSystem>().Enabled = true;
                bot = em.SpawnEntity("BitrunSourceMobLivingBasicMiningDrone", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
                ore = em.SpawnEntity("GoldOre1", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            });
            await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                Assert.That(em.System<SharedContainerSystem>().GetContainer(bot, "bitrun-minebot-ore").ContainedEntities, Does.Contain(ore));
                em.System<BitrunMinebotSystem>().Dump(bot);
                Assert.That(em.System<SharedContainerSystem>().IsEntityInContainer(ore), Is.False);
                em.System<BitrunMinebotSystem>().SetMode((bot, em.GetComponent<BitrunMinebotComponent>(bot)), BitrunMinebotMode.Combat);
                victim = em.SpawnEntity("MobGoliath", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
                em.GetComponent<BitrunMinebotComponent>(bot).MinimumDistance = 0;
                em.GetComponent<BitrunMinebotComponent>(bot).Target = victim;
            });
            await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 3);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                Assert.That(em.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.GreaterThan(0));
                Assert.That(em.GetComponent<RechargeBasicEntityAmmoComponent>(em.GetComponent<BitrunMinebotComponent>(bot).Gun).RechargeCooldown, Is.EqualTo(2));
            });
        }
        finally
        {
            await pair.Server.WaitPost(() =>
            {
                config.SetCVar(CCVars.NPCEnabled, enabled);
                config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, paused);
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CapsuleRetriesBlockedGroundThenDeploysTheSourceShelterAtItsOwnLocation()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid capsule = default, user = default, wall = default;
        await pair.Server.WaitAssertion(() =>
        {
            Floor(pair, map);
            var em = pair.Server.EntMan;
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.AddComponent<GodmodeComponent>(user);
            wall = em.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            capsule = em.SpawnEntity("BitrunSourceObjItemSurvivalcapsule", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, capsule), Is.True);
            em.EventBus.RaiseLocalEvent(capsule, new UseInHandEvent(user));
            Assert.That(em.System<SharedHandsSystem>().TryDrop(user, capsule), Is.True);
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 6);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.EntityExists(capsule), Is.True);
            Assert.That(em.GetComponent<BitrunShelterCapsuleComponent>(capsule).Armed, Is.False);
            em.DeleteEntity(wall);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, capsule), Is.True);
            em.EventBus.RaiseLocalEvent(capsule, new UseInHandEvent(user));
            Assert.That(em.System<SharedHandsSystem>().TryDrop(user, capsule), Is.True);
            em.System<SharedTransformSystem>().SetCoordinates(capsule, map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
        });
        await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 6);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.Deleted(capsule), Is.True, em.Deleted(capsule) ? "" : em.GetComponent<BitrunShelterCapsuleComponent>(capsule).LastFailure);
            var fridges = em.EntityQueryEnumerator<BitrunShelterFridgeComponent, TransformComponent>();
            Assert.That(fridges.MoveNext(out var fridge, out _, out var transform), Is.True);
            Assert.That(transform.GridUid, Is.EqualTo(map.Grid.Owner));
            Assert.That(em.System<SharedContainerSystem>().GetContainer(fridge, "smart_fridge_inventory").ContainedEntities.Count, Is.EqualTo(6));
            em.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
            Assert.That(em.System<AtmosphereSystem>().GetContainingMixture(user, true, true)!.Pressure, Is.GreaterThan(80));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MiningVoucherOffersAllEightSourceSetsAndFilledConscriptBags()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var terminal = em.SpawnEntity("BitrunSourceObjMachineryComputerOrderConsoleMining", map.GridCoords);
            if (em.HasComponent<ApcPowerReceiverComponent>(terminal)) em.RemoveComponent<ApcPowerReceiverComponent>(terminal);
            for (var choice = 0; choice < 8; choice++)
            {
                var voucher = em.SpawnEntity("BitrunSourceObjItemMiningVoucher", map.GridCoords);
                Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, voucher), Is.True);
                var component = em.GetComponent<BitrunEquipmentVoucherComponent>(voucher);
                Assert.That(component.Rewards.Count, Is.EqualTo(8));
                Assert.That(em.System<BitrunVoucherSystem>().Redeem(terminal, voucher, user, choice), Is.True);
                Assert.That(em.System<BitrunVoucherSystem>().Redeem(terminal, voucher, user, choice), Is.False);
                em.DeleteEntity(voucher);
            }
            foreach (var (prototype, count) in new[] {("BitrunSourceObjItemStorageBackpackDuffelbagMiningConscript", 10), ("BitrunSourceObjItemStorageBackpackDuffelbagMiningBunnyConscript", 12)})
            {
                var bag = em.SpawnEntity(prototype, map.GridCoords);
                Assert.That(em.System<SharedContainerSystem>().GetContainer(bag, "storagebase").ContainedEntities.Count, Is.EqualTo(count), prototype);
            }
        });
        await pair.CleanReturnAsync();
    }
}
