using System;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Salvage;
using Content.Server._DeepLagoon.Weapons;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._DeepLagoon.Weapons;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.EntityTable;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.Power.Components;
using Content.Shared.Projectiles;
using Content.Shared.VendingMachines;
using Content.Shared.Weapons.Hitscan.Events;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class VendingWeaponReviewTests
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: DLReviewLaserTarget
  components:
  - type: Transform
    anchored: true
  - type: Damageable
    damageContainer: Inorganic
  - type: Physics
    bodyType: Static
  - type: Fixtures
    fixtures:
      target:
        shape: !type:PhysShapeAabb
          bounds: '-0.2,-0.2,0.2,0.2'
        layer: [Opaque]
";

    [Test]
    public async Task RetailChangesKeepEmagStockAndConnectToRealLootTables()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var loot = entities.System<ExpeditionLootSystem>();
            foreach (var pack in new[] { "WeaponryWorksVendInventory", "WeaponryWorksVendPOIInventory" })
            {
                var inventory = prototypes.Index<VendingMachineInventoryPrototype>(pack);
                foreach (var removed in new[] { "WeaponSubMachineGunAK220", "WeaponLaserPistolNF", "WeaponRifleGestio", "WeaponRifleNovaliteC1", "WeaponRevolverArgenti", "WeaponShotgunDoubleBarreled" })
                    Assert.That(inventory.StartingInventory.ContainsKey(removed), Is.False, $"{pack}: {removed}");
                Assert.That(inventory.EmaggedInventory!.ContainsKey("WeaponPistolViper"), Is.True);
                Assert.That(inventory.StartingInventory.ContainsKey("WeaponPistolMk32"), Is.True);
            }
            Assert.That(prototypes.Index<VendingMachineInventoryPrototype>("NonLethalVendInventory")
                .StartingInventory.ContainsKey("WeaponLaserCarbinePractice"), Is.True);
            Assert.That(prototypes.Index<VendingMachineInventoryPrototype>("WeaponryWorksVendPOIMercInventory")
                .StartingInventory.ContainsKey("WeaponRifleVulcanFCMC"), Is.True);
            var tables = entities.System<EntityTableSystem>();
            var random = new Random(20261030);
            foreach (var tier in new[] { 1, 2, 3 })
            {
                var table = prototypes.Index<EntityTablePrototype>($"DeepLagoonReviewedWeaponsT{tier}");
                Assert.That(table.Table, Is.TypeOf<GroupSelector>());
                foreach (var child in ((GroupSelector) table.Table).Children)
                foreach (var id in tables.GetSpawns(child, random))
                    Assert.That(loot.GetTier(id), Is.EqualTo(tier), id.Id);
                if (tier > 1)
                {
                    var marker = prototypes.Index<EntityPrototype>($"SpawnDungeonLootGunT{tier}");
                    var spawner = (Content.Server.Spawners.Components.RandomSpawnerComponent)
                        marker.Components["RandomSpawner"].Component;
                    Assert.That(spawner.Prototypes, Does.Contain(new EntProtoId($"SpawnDeepLagoonReviewedWeaponsT{tier}")));
                }
            }
            var profile = prototypes.Index<ExpeditionLootPrototype>("DeepLagoonExpeditionLoot");
            Assert.That(loot.GetTier("ClothingOuterHardsuitJuggernaut"), Is.EqualTo(5));
            Assert.That(profile.Tier5Rewards, Does.Contain(new EntProtoId("CrateCybersunJuggernautBundle")));
            Assert.That(profile.Tier4Rewards.Concat(profile.Tier5Rewards).All(id => loot.GetTier(id) >= 4), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task LasersLoseOneQuarterPerCrossedWindow(int windowCount)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        EntityUid map = default;
        EntityUid target = default;
        EntityUid gun = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            map = entities.System<SharedMapSystem>().CreateMap(out _);
            target = entities.SpawnEntity("DLReviewLaserTarget", new EntityCoordinates(map, new Vector2(6, 0)));
            gun = entities.SpawnEntity("WeaponLaserPistolNF", new EntityCoordinates(map, Vector2.Zero));
            for (var i = 0; i < windowCount; i++)
                entities.SpawnEntity(i == 0 ? "Window" : "ReinforcedWindow", new EntityCoordinates(map, new Vector2(2 + 2 * i, 0)));
            // An off-axis window must not attenuate the shot.
            entities.SpawnEntity("Window", new EntityCoordinates(map, new Vector2(3, 3)));
        });
        await pair.RunTicksSync(2);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var expected = MathF.Pow(LaserWindowSystem.WindowTransmission, windowCount);
            var origin = new EntityCoordinates(map, Vector2.Zero);
            var shot = new HitscanRaycastFiredEvent
            {
                FromCoordinates = origin,
                ShotDirection = Vector2.UnitX,
                Gun = gun,
                Shooter = gun,
                HitEntities = new() { target },
                DistanceTried = 6,
            };
            Assert.That(entities.System<LaserWindowSystem>().Transmission(shot, target), Is.EqualTo(expected).Within(0.0001));
            var ray = entities.SpawnEntity("RedMediumLaser", origin);
            entities.EventBus.RaiseLocalEvent(ray, ref shot);
            var damage = entities.GetComponent<DamageableComponent>(target).TotalDamage.Float();
            var modifier = entities.System<DamageableSystem>().UniversalHitscanDamageModifier;
            Assert.That(damage, Is.EqualTo(22 * modifier * expected).Within(0.02));

            var bolt = entities.SpawnEntity("BulletLaser", origin);
            var projectile = entities.GetComponent<ProjectileComponent>(bolt);
            var baseDamage = projectile.Damage.GetTotal().Float();
            entities.System<GunSystem>().ShootProjectile(bolt, Vector2.UnitX, Vector2.Zero, gun, speed: 0);
            entities.System<SharedTransformSystem>().SetCoordinates(bolt, new EntityCoordinates(map, new Vector2(6, 0)));
            var hit = new ProjectileHitEvent(new DamageSpecifier(projectile.Damage), target, gun);
            entities.EventBus.RaiseLocalEvent(bolt, ref hit);
            Assert.That(hit.Damage.GetTotal().Float(), Is.EqualTo(baseDamage * expected).Within(0.02));
            var second = new ProjectileHitEvent(new DamageSpecifier(projectile.Damage), target, gun);
            entities.EventBus.RaiseLocalEvent(bolt, ref second);
            Assert.That(second.Damage.GetTotal().Float(), Is.EqualTo(hit.Damage.GetTotal().Float()).Within(0.001),
                "Repeated collision callbacks must not count the same glass twice.");
            entities.DeleteEntity(map);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrdinaryBatteriesStayEmptyAndEg4RechargesAtHalfSpeed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var weapons = new System.Collections.Generic.List<EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var id in new[] { "WeaponHeavyMiningLaser", "NFWeaponHoloflareGun", "WeaponEnergyShotgun", "WeaponEnergyShotgunOverseer", "WeaponEnergyGunFrost", "WeaponEnergyGunLawbringer" })
            {
                var uid = entities.SpawnEntity(id, MapCoordinates.Nullspace);
                weapons.Add(uid);
                Assert.That(entities.GetComponent<BatterySelfRechargerComponent>(uid).AutoRecharge, Is.False, id);
                Assert.That(entities.HasComponent<HitscanBatteryAmmoProviderComponent>(uid)
                    || entities.HasComponent<ProjectileBatteryAmmoProviderComponent>(uid), Is.True, id);
                entities.System<BatterySystem>().TrySetCharge(uid, 0);
            }
            foreach (var id in new[] { "WeaponEnergyRevolver", "WeaponEnergyRevolverRegistered", "WeaponEnergyRevolverSecurity" })
            {
                var uid = entities.SpawnEntity(id, MapCoordinates.Nullspace);
                var recharge = entities.GetComponent<BatterySelfRechargerComponent>(uid);
                Assert.That(recharge.AutoRecharge, Is.True);
                Assert.That(recharge.AutoRechargeRate, Is.EqualTo(37.5f));
                entities.DeleteEntity(uid);
            }
        });
        await pair.RunTicksSync(90);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var uid in weapons)
            {
                Assert.That(entities.GetComponent<BatteryComponent>(uid).CurrentCharge, Is.Zero);
            }
        });
        var chargers = new System.Collections.Generic.List<EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var uid in weapons)
            {
                var charger = entities.SpawnEntity("PortableRecharger", MapCoordinates.Nullspace);
                chargers.Add(charger);
                Assert.That(entities.System<ItemSlotsSystem>().TryInsert(charger, "charger_slot", uid, null), Is.True,
                    "A depleted gun must fit a native weapon recharger.");
            }
        });
        await pair.RunTicksSync(60);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var uid in weapons)
            {
                Assert.That(entities.GetComponent<BatteryComponent>(uid).CurrentCharge, Is.GreaterThan(0),
                    "Native weapon charging must still replenish disabled self-rechargers.");
                entities.DeleteEntity(uid);
            }
            foreach (var charger in chargers)
            {
                entities.DeleteEntity(charger);
            }
        });
        await pair.CleanReturnAsync();
    }
}
