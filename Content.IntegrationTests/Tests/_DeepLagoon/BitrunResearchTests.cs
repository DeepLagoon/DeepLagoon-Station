using System.Linq;
using Content.Server.Lathe;
using Content.Server.Research.Systems;
using Content.Shared.Interaction;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Research.TechnologyDisk.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class BitrunResearchTests
{
    private static readonly ProtoId<TechnologyPrototype> Bitrunning = "Bitrunning";
    private static readonly ProtoId<TechnologyPrototype> BasicParts = "BasicParts";

    [Test]
    public async Task BitrunningBoardsRequireResearchOnTheLinkedServer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = false });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var research = entities.System<ResearchSystem>();
            var lathe = entities.System<LatheSystem>();
            var technology = prototypes.Index(Bitrunning);
            var prerequisites = prototypes.Index(BasicParts);
            var server = entities.SpawnEntity("ResearchAndDevelopmentServer", MapCoordinates.Nullspace);
            var console = entities.SpawnEntity("ComputerResearchAndDevelopment", MapCoordinates.Nullspace);
            var printer = entities.SpawnEntity("CircuitImprinter", MapCoordinates.Nullspace);
            var actor = entities.SpawnEntity(null, MapCoordinates.Nullspace);

            research.RegisterClient(console, server);
            research.RegisterClient(printer, server);
            research.ModifyServerPoints(server, 100_000);

            Assert.That(technology.RecipeUnlocks, Has.Count.EqualTo(5));
            Assert.That(lathe.TryGetAvailableRecipes(printer, out var before), Is.True);
            Assert.That(technology.RecipeUnlocks.Intersect(before!), Is.Empty);
            Assert.That(research.UnlockTechnology(console, technology, actor), Is.False);

            var disk = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var diskComponent = entities.AddComponent<TechnologyDiskComponent>(disk);
            ProtoId<LatheRecipePrototype> regularRecipe = "TechDiskComputerCircuitboard";
            diskComponent.Recipes = [..technology.RecipeUnlocks, regularRecipe];
            entities.EventBus.RaiseLocalEvent(disk, new AfterInteractEvent(actor, disk, server, EntityCoordinates.Invalid, true));
            var database = entities.GetComponent<TechnologyDatabaseComponent>(server);
            Assert.That(database.UnlockedRecipes, Does.Contain(regularRecipe));
            Assert.That(technology.RecipeUnlocks.Intersect(database.UnlockedRecipes), Is.Empty);

            Assert.That(research.UnlockTechnology(console, prerequisites, actor), Is.True);
            Assert.That(research.UnlockTechnology(console, technology, actor), Is.True);
            Assert.That(lathe.TryGetAvailableRecipes(printer, out var after), Is.True);
            Assert.That(technology.RecipeUnlocks.Except(after!), Is.Empty);
            Assert.That(entities.GetComponent<ResearchServerComponent>(server).Points,
                Is.EqualTo(100_000 - prerequisites.Cost - technology.Cost));

            entities.DeleteEntity(printer);
            entities.DeleteEntity(console);
            entities.DeleteEntity(server);
            entities.DeleteEntity(actor);
        });
        await pair.CleanReturnAsync();
    }
}
