using System.Linq;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BlueMoonRecipeTests
{
    [Test]
    public async Task NativeReactionSystemProducesAllFourReferenceChemicalsAtFourHundredKelvin()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        EntityUid subject = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var solutions = e.System<SharedSolutionContainerSystem>();
            var reactions = e.System<ChemicalReactionSystem>();
            foreach (var (product, reagents, amounts, result) in new[]
            {
                ("DLCrocin", new[] { "Carbon", "Hydrogen", "Oxygen", "Water" }, new[] { 2, 2, 2, 1 }, 6),
                ("DLHexacrocin", new[] { "DLCrocin", "Phenol" }, new[] { 6, 1 }, 1),
                ("DLCamphor", new[] { "Carbon", "Hydrogen", "Oxygen", "Sulfur" }, new[] { 2, 2, 2, 1 }, 6),
                ("DLHexacamphor", new[] { "DLCamphor", "Acetone" }, new[] { 6, 1 }, 1),
            })
            {
                var beaker = e.SpawnEntity("Beaker", MapCoordinates.Nullspace);
                Assert.That(solutions.TryGetRefillableSolution(beaker, out var holder, out var contents), Is.True);
                var input = new Solution { Temperature = 400 };
                for (var i = 0; i < reagents.Length; i++) input.AddReagent(reagents[i], FixedPoint2.New(amounts[i]));
                solutions.TryAddSolution(holder!.Value, input);
                reactions.FullyReactSolution(holder.Value);
                Assert.That(contents!.GetTotalPrototypeQuantity(product), Is.EqualTo(FixedPoint2.New(result)),
                    $"{product}: temperature {contents.Temperature}, products {string.Join(',', contents.Contents.Select(r => r.Reagent.Prototype + ":" + r.Quantity))}");
                e.DeleteEntity(beaker);
            }
            subject = e.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(solutions.TryGetSolution(subject, "chemicals", out var chemicals, out _), Is.True);
            solutions.TryAddSolution(chemicals!.Value, new Solution("DLCrocin", FixedPoint2.New(1)));
        });
        await pair.RunTicksSync(pair.SecondsToTicks(4.1f));
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            Assert.That(e.System<SharedSolutionContainerSystem>().TryGetSolution(subject, "chemicals", out _, out var contents), Is.True);
            Assert.That((float)contents!.GetTotalPrototypeQuantity("DLCrocin"), Is.InRange(.75f, .85f),
                "Default BlueMoon drug consumption is 0.1 units per two-second life tick");
            e.DeleteEntity(subject);
            e.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }
}
