using System.Collections.Generic;
using Content.Server._DeepLagoon.Chemistry;
using Content.Server.EntityEffects.Effects;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared._Goobstation.Clothing.Systems;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Damage;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
[TestOf(typeof(ReagentProtectionSystem))]
public sealed class ReagentProtectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SplashingSlimeWithWaterRespectsPressureSuit(bool wearingSuit)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var slime = entities.SpawnEntity("MobSlimePerson", map.GridCoords);
            var user = entities.SpawnEntity("MobHuman", map.GridCoords);
            var bucket = entities.SpawnEntity("Bucket", map.GridCoords);
            var inventory = entities.System<InventorySystem>();
            if (wearingSuit)
            {
                var suit = entities.SpawnEntity("ClothingOuterHardsuitBasic", map.GridCoords);
                var helmet = entities.SpawnEntity("ClothingHeadHelmetHardsuitBasic", map.GridCoords);
                Assert.That(inventory.TryEquip(slime, suit, "outerClothing", force: true), Is.True);
                Assert.That(inventory.TryEquip(slime, helmet, "head", force: true), Is.True);
            }

            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(bucket, "bucket", out var solutionEntity, out var solution), Is.True);
            Assert.That(solutions.TryAddSolution(solutionEntity.Value, new Solution("Water", 100)), Is.True);
            var beforeVolume = solution.Volume;
            var damage = entities.GetComponent<DamageableComponent>(slime);
            var beforeDamage = damage.TotalDamage;
            var splash = new MeleeHitEvent([slime], user, bucket, new DamageSpecifier(), null);
            entities.EventBus.RaiseLocalEvent(bucket, splash);
            Assert.That(splash.Handled, Is.True);
            Assert.That(solution.Volume, Is.LessThan(beforeVolume));
            Assert.That(damage.TotalDamage - beforeDamage,
                wearingSuit ? Is.EqualTo(FixedPoint2.Zero) : Is.GreaterThan(FixedPoint2.Zero));
            entities.DeleteEntity(bucket);
            entities.DeleteEntity(user);
            entities.DeleteEntity(slime);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("MobSlimePerson", "Water", null, null, false)]
    [TestCase("MobSlimePerson", "Water", "ClothingOuterHardsuitBasic", null, false)]
    [TestCase("MobSlimePerson", "Water", null, "ClothingHeadHelmetHardsuitBasic", false)]
    [TestCase("MobSlimePerson", "Water", "ClothingOuterHardsuitBasic", "ClothingHeadHelmetHardsuitBasic", true)]
    [TestCase("MobSlimePerson", "Water", "ClothingOuterHardsuitEVA", "ClothingHeadHelmetEVA", true)]
    [TestCase("MobHuman", "SulfuricAcid", null, null, false)]
    [TestCase("MobHuman", "SulfuricAcid", "ClothingOuterHardsuitBasic", "ClothingHeadHelmetHardsuitBasic", true)]
    public async Task PressureSuitBlocksContactReactions(
        string mobPrototype, string reagent, string suitPrototype, string helmetPrototype, bool protectedFromContact)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var inventory = entities.System<InventorySystem>();
            var mob = entities.SpawnEntity(mobPrototype, map.GridCoords);
            foreach (var (prototype, slot) in new[] { (suitPrototype, "outerClothing"), (helmetPrototype, "head") })
            {
                if (prototype == null)
                    continue;

                var item = entities.SpawnEntity(prototype, map.GridCoords);
                Assert.That(inventory.TryEquip(mob, item, slot, force: true), Is.True);
            }

            var damage = entities.GetComponent<DamageableComponent>(mob);
            var before = damage.TotalDamage;
            entities.System<ReactiveSystem>().DoEntityReaction(mob, new Solution(reagent, 10), ReactionMethod.Touch);
            Assert.That(damage.TotalDamage - before,
                protectedFromContact ? Is.EqualTo(FixedPoint2.Zero) : Is.GreaterThan(FixedPoint2.Zero));
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ModsuitProtectionFollowsPartSealsAndEquipment()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var inventory = entities.System<InventorySystem>();
            var sealing = entities.System<SharedSealableClothingSystem>();
            var reactive = entities.System<ReactiveSystem>();
            var mob = entities.SpawnEntity("MobSlimePerson", map.GridCoords);
            var chest = entities.SpawnEntity("ClothingModsuitChestplateStandard", map.GridCoords);
            var helmet = entities.SpawnEntity("ClothingModsuitHelmetStandard", map.GridCoords);
            Assert.That(inventory.TryEquip(mob, chest, "outerClothing", force: true), Is.True);
            Assert.That(inventory.TryEquip(mob, helmet, "head", force: true), Is.True);

            void SetSeal(EntityUid item, bool sealedState) =>
                sealing.SetPartSeal((item, entities.GetComponent<SealableClothingComponent>(item)), sealedState);

            void AssertWaterDamage(bool protectedFromContact)
            {
                var damage = entities.GetComponent<DamageableComponent>(mob);
                var before = damage.TotalDamage;
                // Exercise the single-reagent entry point used by chemical smoke as well.
                var solution = new Solution("Water", 10);
                reactive.ReactionEntity(mob, ReactionMethod.Touch, solution.Contents[0], solution);
                Assert.That(damage.TotalDamage - before,
                    protectedFromContact ? Is.EqualTo(FixedPoint2.Zero) : Is.GreaterThan(FixedPoint2.Zero));
            }

            AssertWaterDamage(false);
            SetSeal(chest, true);
            AssertWaterDamage(false);
            SetSeal(helmet, true);
            AssertWaterDamage(true);
            SetSeal(helmet, false);
            AssertWaterDamage(false);
            SetSeal(helmet, true);
            SetSeal(chest, false);
            AssertWaterDamage(false);
            SetSeal(chest, true);
            AssertWaterDamage(true);
            Assert.That(inventory.TryUnequip(mob, "head", force: true), Is.True);
            AssertWaterDamage(false);
            entities.DeleteEntity(helmet);
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(ReactionMethod.Ingestion)]
    [TestCase(ReactionMethod.Injection)]
    public async Task PressureSuitDoesNotBlockInternalReactions(ReactionMethod method)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var mob = entities.SpawnEntity("MobSlimePerson", map.GridCoords);
            var inventory = entities.System<InventorySystem>();
            foreach (var (prototype, slot) in new[]
                     {
                         ("ClothingOuterHardsuitBasic", "outerClothing"),
                         ("ClothingHeadHelmetHardsuitBasic", "head"),
                     })
            {
                var item = entities.SpawnEntity(prototype, map.GridCoords);
                Assert.That(inventory.TryEquip(mob, item, slot, force: true), Is.True);
            }

            // A deterministic internal effect verifies that the contact barrier does not
            // accidentally suppress reactions after a reagent has already entered the body.
            var reactive = entities.GetComponent<ReactiveComponent>(mob);
            reactive.Reactions =
            [
                new ReactiveReagentEffectEntry
                {
                    Methods = [method],
                    Reagents = ["Water"],
                    Effects = new List<EntityEffect>
                    {
                        new HealthChange
                        {
                            Damage = new DamageSpecifier { DamageDict = { ["Heat"] = 1 } },
                        },
                    },
                },
            ];
            var damage = entities.GetComponent<DamageableComponent>(mob);
            var before = damage.TotalDamage;
            entities.System<ReactiveSystem>().DoEntityReaction(mob, new Solution("Water", 10), method);
            Assert.That(damage.TotalDamage, Is.GreaterThan(before));
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }
}
