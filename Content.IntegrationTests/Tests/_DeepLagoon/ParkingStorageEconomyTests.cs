using System;
using System.Linq;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server.Cargo.Systems;
using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Server.Materials;
using Content.Server.Research.Systems;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Construction.Components;
using Content.Shared.Materials;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Content.Shared.Research.TechnologyDisk.Components;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ParkingStorageEconomyTests
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: latheRecipe
          id: DLTestParkingRecipeFirst
          result: Paper
        - type: latheRecipe
          id: DLTestParkingRecipeSecond
          result: Paper
        - type: technology
          id: DLTestParkingSplitDiskTechnology
          name: research-technology-weaponized-laser-manipulation
          discipline: Industrial
          tier: 1
          hidden: true
          position: 999,999
          recipeUnlocks: [DLTestParkingRecipeFirst, DLTestParkingRecipeSecond]
        - type: entity
          id: DLTestParkingRecipeDiskFirst
          components:
          - type: TechnologyDisk
            recipes: [DLTestParkingRecipeFirst]
          - type: StaticPrice
            price: 100
        - type: entity
          id: DLTestParkingRecipeDiskSecond
          components:
          - type: TechnologyDisk
            recipes: [DLTestParkingRecipeSecond]
          - type: StaticPrice
            price: 200
        """;

    [Test]
    public async Task FilteredCloneClearsMaterialsAndUpgradesWithoutMutatingTheSource()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var lathe = entities.SpawnEntity("Autolathe", map.GridCoords);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var construction = entities.System<ConstructionSystem>();
            var machine = entities.GetComponent<MachineComponent>(lathe);
            foreach (var part in construction.GetAllParts(lathe).Where(part => part.Part.PartType == "Manipulator").ToArray())
                entities.DeleteEntity(part.Part.Owner);
            var upgraded = entities.SpawnEntity("PicoManipulatorStockPart", map.GridCoords);
            Assert.That(entities.System<SharedContainerSystem>().Insert(upgraded, machine.PartContainer), Is.True);
            construction.RefreshParts(lathe, machine);
            entities.System<SharedLatheSystem>().SetLatheMultipliers(lathe, materialUse: .5f, time: .25f);
            var storage = entities.GetComponent<MaterialStorageComponent>(lathe);
            Assert.That(entities.System<MaterialStorageSystem>().TryChangeMaterialAmount(lathe, "Steel", 1234,
                storage, localOnly: true), Is.True);
            var price = parking.PreviewStorageSale(map.Grid).Materials;
            Assert.That(price, Is.EqualTo(1234 * pair.Server.ResolveDependency<IPrototypeManager>().Index<MaterialPrototype>("Steel").Price));

            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var loaded = entities.EntityQuery<MachineComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored && entities.GetComponent<MetaDataComponent>(entry.Item1.Owner).EntityPrototype?.ID == "Autolathe").Item1;
            Assert.That(entities.GetComponent<MaterialStorageComponent>(loaded.Owner).Storage, Is.Empty);
            Assert.That(construction.GetAllParts(loaded.Owner).Select(part => part.Part.Rating), Is.All.EqualTo(1));
            var prototype = pair.Server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>("Autolathe");
            Assert.That(prototype.TryGetComponent<LatheComponent>(out var stockLathe, entities.ComponentFactory), Is.True);
            var storedLathe = entities.GetComponent<LatheComponent>(loaded.Owner);
            Assert.That(storedLathe.TimeMultiplier, Is.EqualTo(stockLathe!.TimeMultiplier));
            Assert.That(storedLathe.MaterialUseMultiplier, Is.EqualTo(stockLathe.MaterialUseMultiplier));
            Assert.That(entities.GetComponent<LatheComponent>(lathe).TimeMultiplier, Is.EqualTo(.25f),
                "Normalization must not mutate the live machine before the database commit.");
            Assert.That(storage.Storage["Steel"], Is.EqualTo(1234), "A failed later database transaction must leave live resources untouched.");
            Assert.That(entities.EntityExists(upgraded), Is.True);
            Assert.That(parking.PreviewStorageSale(restored).Materials, Is.Zero, "A redeemed snapshot cannot sell the same resources twice.");
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReplacedShieldNeverDropsBelowThePurchaseBaseline()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var original = entities.SpawnEntity("ShieldGeneratorMedium", map.GridCoords);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            entities.DeleteEntity(original);
            var upgrade = entities.SpawnEntity("ShieldGenerator", map.GridCoords);
            var hiddenWeapon = entities.SpawnEntity("WeaponPistolMk58", map.GridCoords);
            Assert.That(entities.System<SharedContainerSystem>().Insert(hiddenWeapon,
                entities.GetComponent<MachineComponent>(upgrade).PartContainer), Is.True);
            Assert.That(parking.ExcludedEntities(map.Grid), Does.Contain(hiddenWeapon),
                "The installed-material exception must not turn the parts container into weapon storage.");
            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var emitter = entities.EntityQuery<ShipShieldEmitterComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored).Item1;
            Assert.That(entities.GetComponent<MetaDataComponent>(emitter.Owner).EntityPrototype?.ID, Is.EqualTo("ShieldGeneratorMedium"));
            var storedParts = entities.GetComponent<MachineComponent>(emitter.Owner).PartContainer.ContainedEntities;
            Assert.That(storedParts.Any(part => entities.TryGetComponent<StackComponent>(part, out var stack) &&
                stack.StackTypeId == "UraniumFissile" && stack.Count == 1), Is.True,
                "The baseline shield's required fissile stock material must survive validation.");
            Assert.That(parking.ExcludedEntities(restored), Is.Empty);
            Assert.That(entities.EntityQuery<GunComponent, TransformComponent>(true)
                .Any(entry => entry.Item2.GridUid == restored), Is.False);
            Assert.That(entities.GetComponent<MetaDataComponent>(upgrade).EntityPrototype?.ID, Is.EqualTo("ShieldGenerator"));
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DiskEquivalentKnowledgeIsSoldOnceAndUnknownKnowledgeResetsWithoutPayout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings());
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var server = entities.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var disk = entities.SpawnEntity("TechDiskCivAdvancedTools", map.GridCoords);
            var knowledge = entities.GetComponent<TechnologyDiskComponent>(disk).Recipes!;
            var research = entities.System<ResearchSystem>();
            foreach (var recipe in knowledge)
                research.AddLatheRecipe(server, recipe);
            research.AddLatheRecipe(server, "Screwdriver");
            var pricing = entities.System<PricingSystem>();
            var actualPrice = pricing.GetPrice(disk, includeContents: false);
            var estimate = pricing.GetEstimatedPrice(pair.Server.ResolveDependency<IPrototypeManager>().Index<EntityPrototype>("TechDiskCivAdvancedTools"));
            Assert.That(estimate, Is.EqualTo(actualPrice), "Use the existing physical disk's economic value, including zero.");
            var quote = parking.PreviewStorageSale(map.Grid);
            Assert.That(quote.Research, Is.EqualTo(actualPrice));
            Assert.That(quote.UnpricedResearch, Is.GreaterThan(0));
            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var database = entities.EntityQuery<TechnologyDatabaseComponent, TransformComponent>(true)
                .Single(entry => entry.Item2.GridUid == restored && entities.HasComponent<ResearchServerComponent>(entry.Item1.Owner)).Item1;
            Assert.That(database.UnlockedRecipes, Does.Not.Contain(knowledge[0]));
            Assert.That(database.UnlockedRecipes, Does.Not.Contain((Robust.Shared.Prototypes.ProtoId<Content.Shared.Research.Prototypes.LatheRecipePrototype>) "Screwdriver"));
            Assert.That(entities.GetComponent<TechnologyDatabaseComponent>(server).UnlockedRecipes, Does.Contain(knowledge[0]));
            Assert.That(parking.PreviewStorageSale(restored).Research, Is.Zero);
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TechnologyCoveredByTwoSeparateDisksIsPricedOnceAcrossMirroredServers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var first = entities.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords);
            var second = entities.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords);
            var research = entities.System<ResearchSystem>();
            research.AddTechnology(first, "DLTestParkingSplitDiskTechnology");
            research.Sync(second, first);
            var parking = entities.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, Guid.NewGuid(), 1000, "", "Captain");
            var quote = parking.PreviewStorageSale(map.Grid);
            Assert.That(quote.Research, Is.EqualTo(300),
                "A technology can be represented by two separate recipe disks; mirrored databases are not extra assets.");
            Assert.That(quote.UnpricedResearch, Is.Zero);
            var restored = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            Assert.That(parking.PreviewStorageSale(restored).Research, Is.Zero);
            foreach (var (database, transform) in entities.EntityQuery<TechnologyDatabaseComponent, TransformComponent>(true))
                if (transform.GridUid == restored)
                {
                    Assert.That(database.UnlockedTechnologies.Select(tech => tech.Id), Does.Not.Contain("DLTestParkingSplitDiskTechnology"));
                    Assert.That(database.UnlockedRecipes.Select(recipe => recipe.Id), Does.Not.Contain("DLTestParkingRecipeFirst"));
                    Assert.That(database.UnlockedRecipes.Select(recipe => recipe.Id), Does.Not.Contain("DLTestParkingRecipeSecond"));
                }
            entities.DeleteEntity(restored);
        });
        await pair.CleanReturnAsync();
    }
}
