using System.Linq;
using Content.Server._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BodyCustomizationRuntimeTests
{
    private static readonly string[] Reagents = ["DLButtGrowth", "DLButtReduction", "DLBellyGrowth", "DLBellyReduction",
        "DLBreastsGrowth", "DLBreastsReduction", "DLAnusGrowth", "DLAnusReduction", "DLPenisGrowth", "DLPenisReduction"];

    [Test]
    public async Task RecipesRequireHeatHaveNoCompetingRecipesAndPillsContainOneDose()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var solutions = entities.System<SharedSolutionContainerSystem>();
            foreach (var id in Reagents)
            {
                var recipe = prototypes.Index<ReactionPrototype>(id);
                Assert.That(recipe.MinimumTemperature, Is.EqualTo(370));
                Assert.That(recipe.Reactants.ContainsKey("Silicon"), Is.True);
                var ingredients = recipe.Reactants.Keys.ToHashSet();
                foreach (var other in prototypes.EnumeratePrototypes<ReactionPrototype>().Where(r => r.ID != id && r.Reactants.Count > 0))
                {
                    Assert.That(other.Reactants.Keys.All(ingredients.Contains), Is.False, $"Competing recipe: {other.ID}");
                    Assert.That(ingredients.All(other.Reactants.ContainsKey), Is.False, $"Recipe subset: {other.ID}");
                }
                var beaker = entities.SpawnEntity("Beaker", MapCoordinates.Nullspace);
                Assert.That(solutions.TryGetSolution(beaker, "beaker", out var solutionEntity, out var solution), Is.True);
                solutions.SetTemperature(solutionEntity.Value, 293);
                foreach (var (ingredient, reactant) in recipe.Reactants)
                    solutions.TryAddReagent(solutionEntity.Value, ingredient, reactant.Amount, out _);
                Assert.That(solution.GetReagentQuantity(new ReagentId(id, null)), Is.EqualTo(FixedPoint2.Zero), "Cold ingredients must not react");
                Assert.That(solution.Contents, Has.Count.EqualTo(3));
                solutions.SetTemperature(solutionEntity.Value, 370);
                Assert.That(solution.GetReagentQuantity(new ReagentId(id, null)), Is.EqualTo(FixedPoint2.New(3)));
                Assert.That(solution.Contents, Has.Count.EqualTo(1));
                var pill = entities.SpawnEntity("Pill" + id, MapCoordinates.Nullspace);
                Assert.That(solutions.TryGetSolution(pill, "food", out _, out var pillSolution), Is.True);
                Assert.That(pillSolution.GetReagentQuantity(new ReagentId(id, null)), Is.EqualTo(FixedPoint2.New(5)));
                var canister = entities.SpawnEntity("PillCanister" + id, MapCoordinates.Nullspace);
                entities.DeleteEntity(canister);
                entities.DeleteEntity(pill);
                entities.DeleteEntity(beaker);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MetabolismUsesActualDoseAndFullCatalogWithoutChangingPreferences()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var uid = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
            var original = BodyCustomizationCatalog.Order.Select(BodyCustomizationCatalog.Default).ToList();
            foreach (var feature in original) feature.Enabled = true;
            humanoid.BodyFeatures = BodyCustomizationCatalog.SanitizeForEditor(original);
            var system = entities.System<BodyCustomizationSystem>();
            foreach (var id in Reagents)
            {
                var reagent = prototypes.Index<ReagentPrototype>(id);
                var effect = (ChangeBodyFeatureSize) reagent.Metabolisms!.Single().Value.Effects.Single();
                var feature = humanoid.BodyFeatures.Single(f => f.Part == effect.Part);
                var sizes = BodyCustomizationCatalog.Sizes(feature.Part, feature.Shape);
                feature.Size = effect.Direction == 1 ? BodyCustomizationCatalog.EditorSizes(feature.Part, feature.Shape).Last() : sizes.Last();
                feature.Visibility = 0;
                var before = Array.IndexOf(sizes, feature.Size);
                var step = Math.Clamp(before + effect.Direction, 0, sizes.Length - 1);
                // Partial metabolism ticks and injections must count by volume, not invocation count.
                for (var i = 0; i < 49; i++)
                    effect.Effect(new EntityEffectReagentArgs(uid, entities, null, null, FixedPoint2.New(0.1f), reagent, null, 1));
                Assert.That(humanoid.BodyFeatures.Single(f => f.Part == effect.Part).Size, Is.EqualTo(sizes[before]));
                effect.Effect(new EntityEffectReagentArgs(uid, entities, null, null, FixedPoint2.New(0.1f), reagent, null, 1));
                Assert.That(humanoid.BodyFeatures.Single(f => f.Part == effect.Part).Size, Is.EqualTo(sizes[step]));
                Assert.That(effect.GuidebookEffectDescription(prototypes, entities.EntitySysManager), Is.Not.Empty);
                system.ApplyDose(uid, effect.Part, effect.Direction, 100);
                Assert.That(humanoid.BodyFeatures.Single(f => f.Part == effect.Part).Size,
                    Is.EqualTo(effect.Direction == 1 ? sizes.Last() : sizes.First()));
            }
            Assert.That(original.Single(f => f.Part == "breasts").Size, Is.EqualTo("a"));
            Assert.That(system.SetVisibility(uid, "breasts", 2), Is.True);
            Assert.That(system.SetVisibility(uid, "breasts", 3), Is.False);
            Assert.That(system.SetVisibility(uid, "womb", 1), Is.False);
            Assert.That(system.SetVisibility(uid, "missing", 1), Is.False);
            humanoid.BodyFeatures.Single(f => f.Part == "penis").Enabled = false;
            Assert.That(system.ApplyDose(uid, "penis", 1, 5), Is.False);
            Assert.That(system.SetVisibility(uid, "penis", 1), Is.False);
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VisibilityNetworkRequestsRequireOpenPanelAndOnlyChangeOwnCharacter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid other = default;
        NetEntity target = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            other = entities.SpawnEntity("MobHuman", map.GridCoords);
            foreach (var uid in new[] { user, other })
            {
                var feature = BodyCustomizationCatalog.Default("breasts"); feature.Enabled = true;
                entities.GetComponent<HumanoidAppearanceComponent>(uid).BodyFeatures = [feature];
            }
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            target = entities.GetNetEntity(user);
        });
        await pair.RunTicksSync(30);
        async Task Send(EntityEventArgs message)
        {
            await pair.Client.WaitPost(() => pair.Client.EntMan.EntityNetManager!.SendSystemNetworkMessage(message));
            await pair.RunTicksSync(20);
        }
        await Send(new InteractionPanelBodyVisibilityEvent("breasts", 2));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures.Single().Visibility, Is.EqualTo(1)));
        await Send(new InteractionPanelOpenEvent(target));
        await Send(new InteractionPanelBodyVisibilityEvent("breasts", 2));
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures.Single().Visibility, Is.EqualTo(2));
            Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(other).BodyFeatures.Single().Visibility, Is.EqualTo(1));
        });
        await Send(new InteractionPanelBodyVisibilityEvent("breasts", 9));
        await Send(new InteractionPanelCloseEvent(target));
        await Send(new InteractionPanelBodyVisibilityEvent("breasts", 0));
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures.Single().Visibility, Is.EqualTo(2)));
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null));
        await pair.CleanReturnAsync();
    }
}
