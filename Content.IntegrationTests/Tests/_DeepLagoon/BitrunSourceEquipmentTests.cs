using System.Linq;
using System.Collections.Generic;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Damage.Components;
using Content.Shared.Damage;
using Content.Server.NPC.Systems;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using System.Numerics;
using Content.Server.Mind;
using Robust.Shared.Physics.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunSourceEquipmentTests
{
    [TestCase("BitrunSourceMobLivingBasicHivebotRange")]
    [TestCase("BitrunSourceMobLivingBasicMimicCrate")]
    public async Task SourceHostilesActuallyAttackATarget(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        EntityUid target = default;
        EntityUid npc = default;
        var config = pair.Server.ResolveDependency<IConfigurationManager>();
        var previousEnabled = config.GetCVar(CCVars.NPCEnabled);
        var previousPause = config.GetCVar(CCVars.NPCPauseWhenNoPlayersInRange);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var maps = entities.System<SharedMapSystem>();
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId);
            maps.SetTiles(map.Grid, map.Grid.Comp, Enumerable.Range(-3, 10).SelectMany(x =>
                Enumerable.Range(-3, 10).Select(y => (new Vector2i(x, y), tile))).ToList());
            config.SetCVar(CCVars.NPCEnabled, true);
            config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, false);
            entities.System<NPCSystem>().Enabled = true;
            target = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            var minds = entities.System<MindSystem>();
            minds.TransferTo(minds.CreateMind(null), target);
            npc = entities.SpawnEntity(prototype, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
        });
        try
        {
            await pair.Server.WaitRunTicks(pair.Server.Timing.TickRate * 5);
            await pair.Server.WaitAssertion(() =>
            {
                var damage = pair.Server.EntMan.GetComponent<DamageableComponent>(target);
                Assert.That((float) damage.TotalDamage, Is.GreaterThan(0), prototype);
            });
        }
        finally
        {
            await pair.Server.WaitPost(() =>
            {
                pair.Server.EntMan.DeleteEntity(target);
                pair.Server.EntMan.DeleteEntity(npc);
                config.SetCVar(CCVars.NPCEnabled, previousEnabled);
                config.SetCVar(CCVars.NPCPauseWhenNoPlayersInRange, previousPause);
            });
        }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SourceDisablerInflictsStaminaWithoutLethalDamage()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var projectile = entities.SpawnEntity("BitrunSourceObjProjectileBeamDisabler", map.GridCoords);
            Assert.That((float) entities.GetComponent<ProjectileComponent>(projectile).Damage.GetTotal(), Is.EqualTo(0));
            Assert.That(entities.GetComponent<StaminaDamageOnCollideComponent>(projectile).Damage, Is.EqualTo(30));
            var target = entities.SpawnEntity("MobHuman", map.GridCoords);
            var bullet = entities.GetComponent<ProjectileComponent>(projectile);
            var physics = entities.GetComponent<PhysicsComponent>(projectile);
            entities.System<SharedProjectileSystem>().ProjectileCollide((projectile, bullet, physics), target);
            Assert.That(entities.GetComponent<StaminaComponent>(target).StaminaDamage, Is.EqualTo(30));
            Assert.That((float) entities.GetComponent<DamageableComponent>(target).TotalDamage, Is.EqualTo(0));
            entities.DeleteEntity(projectile);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EnergyModesChangeTheActualProjectileAndChargeCost()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var modes = entities.System<BatteryWeaponFireModesSystem>();
            var checkedGuns = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>()
                         .Where(p => p.ID.StartsWith("BitrunSourceObjItemGun")))
            {
                var gun = entities.SpawnEntity(prototype.ID, map.GridCoords);
                if (!entities.TryGetComponent<BatteryWeaponFireModesComponent>(gun, out var settings))
                {
                    entities.DeleteEntity(gun);
                    continue;
                }
                checkedGuns++;
                for (var index = 0; index < settings.FireModes.Count; index++)
                {
                    Assert.That(modes.TrySetFireMode(gun, settings, index), Is.True);
                    var provider = entities.GetComponent<ProjectileBatteryAmmoProviderComponent>(gun);
                    Assert.That(provider.Prototype, Is.EqualTo(settings.FireModes[index].Prototype));
                    Assert.That(provider.FireCost, Is.EqualTo(settings.FireModes[index].FireCost));
                    var take = new TakeAmmoEvent(1, [], map.GridCoords, null);
                    entities.EventBus.RaiseLocalEvent(gun, take);
                    Assert.That(take.Ammo, Has.Count.EqualTo(1), prototype.ID);
                    var projectile = take.Ammo[0].Entity!.Value;
                    Assert.That(entities.GetComponent<MetaDataComponent>(projectile).EntityPrototype!.ID,
                        Is.EqualTo(settings.FireModes[index].Prototype.Id));
                    entities.DeleteEntity(projectile);
                }
                entities.DeleteEntity(gun);
            }
            Assert.That(checkedGuns, Is.GreaterThanOrEqualTo(2));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SourceClothesActuallyEquipInTheirAuthoredSlots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var inventory = entities.System<InventorySystem>();
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var families = new Dictionary<string, string>
            {
                ["Under"] = "jumpsuit", ["Suit"] = "outerClothing", ["Head"] = "head", ["Mask"] = "mask",
                ["Glasses"] = "eyes", ["Gloves"] = "gloves", ["Shoes"] = "shoes", ["Neck"] = "neck",
            };
            var checkedClothes = 0;
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>()
                         .Where(p => p.ID.StartsWith("BitrunMapItemClothing")))
            {
                var family = families.FirstOrDefault(pair => prototype.ID.StartsWith("BitrunMapItemClothing" + pair.Key));
                if (family.Value == null) continue;
                var clothing = entities.SpawnEntity(prototype.ID, map.GridCoords);
                Assert.That(entities.HasComponent<ClothingComponent>(clothing), Is.True, prototype.ID);
                Assert.That(inventory.TryEquip(human, clothing, family.Value, silent: true), Is.True, prototype.ID);
                checkedClothes++;
                entities.DeleteEntity(clothing);
            }
            Assert.That(checkedClothes, Is.GreaterThan(100));
            entities.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SourceWeaponsSupplyTheirOwnAmmunition()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var guns = prototypes.EnumeratePrototypes<EntityPrototype>()
                .Where(p => p.ID.StartsWith("BitrunSourceObjItemGun")).ToArray();
            Assert.That(guns.Length, Is.GreaterThanOrEqualTo(30));
            foreach (var prototype in guns)
            {
                var uid = entities.SpawnEntity(prototype.ID, map.GridCoords);
                var take = new TakeAmmoEvent(1, [], map.GridCoords, null);
                entities.EventBus.RaiseLocalEvent(uid, take);
                Assert.That(take.Ammo, Has.Count.EqualTo(1), prototype.ID);
                var ammo = take.Ammo[0].Entity!.Value;
                if (entities.TryGetComponent<CartridgeAmmoComponent>(ammo, out var cartridge))
                {
                    Assert.That(cartridge.Prototype.Id, Does.StartWith("Bitrun"), prototype.ID);
                    var projectile = entities.SpawnEntity(cartridge.Prototype, map.GridCoords);
                    Assert.That(entities.HasComponent<ProjectileComponent>(projectile), Is.True, prototype.ID);
                    entities.DeleteEntity(projectile);
                }
                else
                {
                    Assert.That(entities.HasComponent<ProjectileComponent>(ammo), Is.True, prototype.ID);
                }
                entities.DeleteEntity(ammo);
                entities.DeleteEntity(uid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MagazinesUseSourceCapacityAndRejectTheWrongType()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var slots = entities.System<ItemSlotsSystem>();
            var pistol = entities.SpawnEntity("BitrunSourceObjItemGunBallisticAutomaticPistolSol", map.GridCoords);
            Assert.That(slots.TryGetSlot(pistol, "gun_magazine", out var magazine), Is.True);
            var ammo = entities.GetComponent<BallisticAmmoProviderComponent>(magazine!.Item!.Value);
            Assert.That(ammo.Capacity, Is.EqualTo(12));
            Assert.That(ammo.UnspawnedCount, Is.EqualTo(11), "The chambered round comes out of the magazine.");
            Assert.That(slots.TryEject(pistol, "gun_magazine", null, out var previous), Is.True);
            var wrong = entities.SpawnEntity("BitrunSourceObjItemAmmoBoxMagazineMiecz", map.GridCoords);
            Assert.That(slots.TryInsert(pistol, "gun_magazine", wrong, null), Is.False);
            var extended = entities.SpawnEntity("BitrunSourceObjItemAmmoBoxMagazineC35solPistolStendo", map.GridCoords);
            Assert.That(slots.TryInsert(pistol, "gun_magazine", extended, null), Is.True,
                "A subtype of the accepted source magazine remains compatible.");
            entities.DeleteEntity(previous!.Value);
            entities.DeleteEntity(wrong);
            entities.DeleteEntity(pistol);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KineticModkitsAffectActualShotsAndCanBeRecovered()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var user = entities.SpawnEntity("MobHuman", map.GridCoords);
            var gun = entities.SpawnEntity("BitrunSourceObjItemGunEnergyRechargeKineticAccelerator", map.GridCoords);
            var host = (gun, entities.GetComponent<BitrunKineticAcceleratorComponent>(gun));
            var mods = entities.System<BitrunKineticSystem>();
            var hands = entities.System<SharedHandsSystem>();
            var installed = new List<EntityUid>();
            foreach (var kind in new[] { "Damage", "Range", "Cooldown" })
            {
                var kit = entities.SpawnEntity("BitrunSourceObjItemBorgUpgradeModkit" + kind, map.GridCoords);
                Assert.That(hands.TryPickupAnyHand(user, kit), Is.True);
                Assert.That(mods.TryInstall(host, kit, user), Is.True);
                installed.Add(kit);
            }
            Assert.That(mods.UsedCapacity(gun), Is.EqualTo(85));
            var extra = entities.SpawnEntity("BitrunSourceObjItemBorgUpgradeModkitDamage", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, extra), Is.True);
            Assert.That(mods.TryInstall(host, extra, user), Is.False);
            var recharge = entities.GetComponent<RechargeBasicEntityAmmoComponent>(gun);
            Assert.That(recharge.RechargeCooldown, Is.EqualTo(1.28f).Within(0.001f));
            entities.System<GunSystem>().AttemptShoot(gun, entities.GetComponent<GunComponent>(gun));
            var query = entities.EntityQueryEnumerator<ProjectileComponent>();
            EntityUid bolt = default;
            while (query.MoveNext(out var uid, out var projectile))
            {
                if (projectile.Weapon != gun) continue;
                bolt = uid;
                Assert.That((float) projectile.Damage.GetTotal(), Is.EqualTo(50));
                Assert.That(entities.GetComponent<BitrunProjectileRangeComponent>(uid).Range, Is.EqualTo(4));
            }
            Assert.That(bolt, Is.Not.EqualTo(default(EntityUid)), "A real firing attempt must produce a projectile.");
            entities.DeleteEntity(bolt);
            foreach (var kit in installed)
            {
                Assert.That(mods.Remove(host, kit, user), Is.True);
                Assert.That(entities.EntityExists(kit), Is.True);
                entities.DeleteEntity(kit);
            }
            Assert.That(recharge.RechargeCooldown, Is.EqualTo(1.6f).Within(0.001f));
            entities.DeleteEntity(extra);
            entities.DeleteEntity(gun);
            entities.DeleteEntity(user);
        });
        await pair.CleanReturnAsync();
    }
}
