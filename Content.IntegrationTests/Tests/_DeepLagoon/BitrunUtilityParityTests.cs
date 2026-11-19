using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.Bitrunning;
using Content.Shared.Armor;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BitrunUtilityParityTests
{
    [Test]
    public async Task DishDriveUsesInstalledPartTiersAndLeavesLoadedPlatesAlone()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid drive = default, glass = default, plate = default;
        await pair.Server.WaitAssertion(() =>
        {
            for (var x = 0; x <= 6; x++) em.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(x, 0), map.Tile.Tile);
            drive = em.SpawnEntity("BitrunDishDrive", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            var comp = em.GetComponent<BitrunDishDriveComponent>(drive);
            Assert.That(comp.SuctionRange, Is.EqualTo(3));
            var receiver = em.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(drive);
            Assert.That(receiver.Load, Is.EqualTo(37));
            var machine = em.GetComponent<Content.Server.Construction.Components.MachineComponent>(drive);
            foreach (var item in machine.PartContainer.ContainedEntities.ToArray())
                if (em.HasComponent<Content.Shared.Construction.Components.MachinePartComponent>(item)) em.DeleteEntity(item);
            var containers = em.System<Robust.Shared.Containers.SharedContainerSystem>();
            foreach (var proto in new[] { "NanoManipulatorStockPart", "AdvancedMatterBinStockPart", "AdvancedMatterBinStockPart" })
                Assert.That(containers.Insert(em.SpawnEntity(proto, map.GridCoords), machine.PartContainer), Is.True);
            em.System<Content.Server.Construction.ConstructionSystem>().RefreshParts(drive, machine);
            Assert.That(comp.SuctionRange, Is.EqualTo(5));
            Assert.That(receiver.Load, Is.Zero);
            em.RemoveComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(drive);
            glass = em.SpawnEntity("DrinkGlass", map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
            plate = em.SpawnEntity("FoodPlateSmall", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            var contents = containers.EnsureContainer<Robust.Shared.Containers.Container>(plate, "food-on-plate");
            Assert.That(containers.Insert(em.SpawnEntity("FoodApple", map.GridCoords), contents), Is.True);
        });
        await pair.RunSeconds(9);
        await pair.Server.WaitAssertion(() =>
        {
            var containers = em.System<Robust.Shared.Containers.SharedContainerSystem>();
            Assert.That(containers.GetContainer(drive, "bitrun-dishes").ContainedEntities, Does.Contain(glass));
            Assert.That(containers.IsEntityInContainer(plate), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SourceHelmetProtectsItsBodyPartAndFireBypassReachesTheActualLimb()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var damage = em.System<Content.Shared.Damage.DamageableSystem>();
            var body = em.System<Content.Shared.Body.Systems.SharedBodySystem>();
            foreach (var (part, type, ignore, expectedRoot, expectedPart) in new[]
            {
                (Content.Shared._Shitmed.Targeting.TargetBodyPart.Torso, "Blunt", false, 100f, 80f),
                (Content.Shared._Shitmed.Targeting.TargetBodyPart.Head, "Blunt", false, 20f, 16f),
                (Content.Shared._Shitmed.Targeting.TargetBodyPart.Head, "Heat", true, 50f, 40f),
                (Content.Shared._Shitmed.Targeting.TargetBodyPart.Head, "Heat", false, 45f, 36f),
            })
            {
                var user = em.SpawnEntity("MobHuman", map.GridCoords);
                var helmet = em.SpawnEntity(null, map.GridCoords);
                em.AddComponent<ItemComponent>(helmet);
                var armor = em.AddComponent<ArmorComponent>(helmet);
                armor.Coverage = Content.Shared._Shitmed.Targeting.TargetBodyPart.Head;
                armor.Modifiers = new Content.Shared.Damage.DamageModifierSet { Coefficients = { ["Blunt"] = 0.4f, ["Heat"] = 0.6f } };
                Assert.That(em.System<InventorySystem>().TryEquip(user, helmet, "head", force: true), Is.True);
                EntityUid? tool = null;
                if (ignore || type == "Heat")
                {
                    tool = em.SpawnEntity(null, map.GridCoords);
                    var damageArmor = em.AddComponent<DamageArmorOverrideComponent>(tool.Value);
                    damageArmor.IgnoreArmor = ignore;
                    damageArmor.SourceArmorPenetration = true;
                    if (!ignore) damageArmor.ArmorType = "Blunt";
                }
                var changed = damage.TryChangeDamage(user, new Content.Shared.Damage.DamageSpecifier { DamageDict = { [type] = 100 } },
                    targetPart: part, canSever: false, tool: tool, armorPenetration: type == "Heat" && !ignore ? 0.5f : 0);
                Assert.That(changed!.GetTotal().Float(), Is.EqualTo(expectedRoot).Within(0.01f));
                var limb = body.GetBodyChildrenOfType(user, part == Content.Shared._Shitmed.Targeting.TargetBodyPart.Head ?
                    Content.Shared.Body.Part.BodyPartType.Head : Content.Shared.Body.Part.BodyPartType.Torso)!.Single().Id;
                Assert.That(em.GetComponent<Content.Shared.Damage.DamageableComponent>(limb).Damage.DamageDict[type].Float(),
                    Is.EqualTo(expectedPart).Within(0.01f));
            }
            var giant = em.SpawnEntity("BitrunMobColossus", map.GridCoords);
            em.GetComponent<Content.Server.NPC.HTN.HTNComponent>(giant).Enabled = false;
            var blast = em.SpawnEntity(null, map.GridCoords);
            em.AddComponent<DamageArmorOverrideComponent>(blast).SourceArmorPenetration = true;
            var innate = damage.TryChangeDamage(giant, new Content.Shared.Damage.DamageSpecifier { DamageDict = { ["Heat"] = 10 } },
                tool: blast, armorPenetration: 0.5f);
            Assert.That(innate!.GetTotal().Float(), Is.EqualTo(5), "Source penetration must not bypass innate megafauna burn resistance.");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BeaconsRejectDuplicatePlacementAndInterruptibleRetrievalMergesIntoHeldStack()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var em = pair.Server.EntMan;
        EntityUid user = default, stack = default, beacon = default;
        await pair.Server.WaitAssertion(() =>
        {
            em.System<SharedMapSystem>().SetTile(map.Grid, new Vector2i(1, 0), map.Tile.Tile);
            user = em.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            em.AddComponent<GodmodeComponent>(user);
            stack = em.SpawnEntity("BitrunSourceObjItemStackMarkerBeaconTen", map.GridCoords);
            Assert.That(em.System<SharedHandsSystem>().TryPickupAnyHand(user, stack), Is.True);
            var colors = em.System<BitrunMarkerBeaconSystem>();
            colors.SetColor((stack, em.GetComponent<BitrunBeaconColorComponent>(stack)), "Violet");
            void Place()
            {
                var use = new UseInHandEvent(user);
                em.EventBus.RaiseLocalEvent(stack, use);
                Assert.That(use.Handled, Is.True);
            }
            Place(); Place();
            Assert.That(em.GetComponent<StackComponent>(stack).Count, Is.EqualTo(9));
            beacon = em.EntityQuery<BitrunPlacedBeaconComponent>().Single().Owner;
            Assert.That(em.GetComponent<BitrunBeaconColorComponent>(beacon).Color, Is.EqualTo("Violet"));
            Assert.That(colors.Retrieve(beacon, user, stack), Is.True);
        });
        await pair.RunSeconds(0.6f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.Deleted(beacon), Is.False);
            em.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
        });
        await pair.RunSeconds(1.5f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.Deleted(beacon), Is.False, "Walking interrupts retrieval.");
            em.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            Assert.That(em.System<BitrunMarkerBeaconSystem>().Retrieve(beacon, user, stack), Is.True);
        });
        await pair.RunSeconds(1.7f);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(em.Deleted(beacon), Is.True);
            Assert.That(em.GetComponent<StackComponent>(stack).Count, Is.EqualTo(10));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SilverBatonPenetratesThirtyPointsOfMeleeArmorBeforeApplyingStamina()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", map.GridCoords);
            var target = em.SpawnEntity("MobHuman", map.GridCoords.Offset(Vector2.UnitX));
            var armor = em.SpawnEntity(null, map.GridCoords);
            em.AddComponent<ItemComponent>(armor);
            em.AddComponent<ArmorComponent>(armor).Modifiers = new Content.Shared.Damage.DamageModifierSet { Coefficients = { ["Blunt"] = 0.4f } };
            Assert.That(em.System<InventorySystem>().TryEquip(target, armor, "outerClothing", force: true), Is.True);
            var baton = em.SpawnEntity("BitrunSourceObjItemMeleeBatonTelescopicSilver", map.GridCoords);
            Assert.That(em.System<ItemToggleSystem>().TryActivate(baton, user), Is.True);
            var hit = new MeleeHitEvent(new() { target }, user, baton, new(), null);
            em.EventBus.RaiseLocalEvent(baton, hit);
            Assert.That(em.GetComponent<StaminaComponent>(target).StaminaDamage, Is.EqualTo(38.5f).Within(0.01f));
        });
        await pair.CleanReturnAsync();
    }
}
