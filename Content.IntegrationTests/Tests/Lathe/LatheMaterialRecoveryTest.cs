using System.Linq;
using System.Numerics;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Nutrition.Components;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Lathe;

[TestFixture]
public sealed class LatheMaterialRecoveryTest
{
    [TestCase("ClothingUniformJumpsuitCargo")]
    [TestCase("ClothingUniformJumpskirtCargo")]
    public async Task PrintingAndCuttingUniformConservesCloth(string recipeId)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var entities = server.EntMan;
        await server.WaitAssertion(() =>
        {
            var printer = entities.SpawnEntity("UniformPrinter", map.GridCoords);
            var userCoords = map.GridCoords.Offset(Vector2.UnitX);
            entities.System<SharedMapSystem>().SetTile(map.Grid, map.Grid.Comp, userCoords, map.Tile.Tile);
            var user = entities.SpawnEntity("MobHuman", userCoords);
            entities.GetComponent<ApcPowerReceiverComponent>(printer).Powered = true;
            var lathe = entities.System<LatheSystem>();
            lathe.SetLatheMultipliers(printer, materialUse: 0.805f, time: 0f);
            var materials = entities.System<SharedMaterialStorageSystem>();
            var recipe = server.ProtoMan.Index<LatheRecipePrototype>(recipeId);

            Assert.That(materials.TryChangeMaterialAmount(printer, "Cloth", 242), Is.True);
            Assert.That(lathe.CanProduce(printer, recipe), Is.False);
            Assert.That(lathe.TryAddToQueue(printer, recipe, 1), Is.False);

            Assert.That(materials.TryChangeMaterialAmount(printer, "Cloth", 58), Is.True);
            Assert.That(lathe.TryAddToQueue(printer, recipe, 1), Is.True);
            Assert.That(lathe.GetEndMaterialAmounts(printer)[new ProtoId<MaterialPrototype>("Cloth")], Is.Zero);
            Assert.That(lathe.TryStartProducing(printer), Is.True);
            Assert.That(materials.GetMaterialAmount(printer, "Cloth"), Is.Zero);

            var uniform = entities.EntityQuery<MetaDataComponent>().Single(meta => meta.EntityPrototype?.ID == recipeId).Owner;
            entities.GetComponent<ButcherableComponent>(uniform).ButcherDelay = 0f;
            var hands = entities.System<SharedHandsSystem>();
            // Both cuts complete before entity deletion is processed.
            for (var i = 0; i < 2; i++)
            {
                var knife = entities.SpawnEntity("SurvivalKnife", userCoords);
                Assert.That(hands.TryPickupAnyHand(user, knife), Is.True);
                var cut = new AfterInteractEvent(user, knife, uniform, map.GridCoords, canReach: true);
                entities.EventBus.RaiseLocalEvent(knife, cut);
                Assert.That(cut.Handled, Is.True);
            }

            Assert.That(entities.IsQueuedForDeletion(uniform), Is.True);
            var cloth = entities.EntityQuery<MetaDataComponent>()
                .Where(meta => meta.EntityPrototype?.ID == "MaterialCloth1")
                .Select(meta => meta.Owner)
                .ToArray();
            foreach (var roll in cloth)
                Assert.That(materials.TryInsertMaterialEntity(user, roll, printer), Is.True);

            Assert.That(materials.GetMaterialAmount(printer, "Cloth"), Is.EqualTo(300));
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("BeltMedical", "Cloth", 200)]
    [TestCase("UniformPrinterMachineCircuitboard", "Glass", 230)]
    [TestCase("UniformPrinterMachineCircuitboard", "Steel", 28)]
    [TestCase("Jug", "Plastic", 200)]
    [TestCase("RiotShield", "Steel", 400)]
    [TestCase("RiotShield", "Glass", 400)]
    public async Task DiscountRespectsMaterialRecovery(string recipeId, string materialId, int expected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var recipe = pair.Server.ProtoMan.Index<LatheRecipePrototype>(recipeId);
            var lathe = pair.Server.EntMan.System<SharedLatheSystem>();
            var material = new ProtoId<MaterialPrototype>(materialId);
            Assert.That(lathe.GetMaterialCost(recipe, material, recipe.Materials[material], 0.28f), Is.EqualTo(expected));
        });
        await pair.CleanReturnAsync();
    }
}
