using System.Linq;
using System.Reflection;
using Content.Server._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Console;
using Robust.Client.GameObjects;
using Moq;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BodyCustomizationRuntimeTests
{
    [Test]
    public async Task ArousalCommandOnlyChangesOwnBodyAndReplicatesBothSpriteStates()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid other = default;
        NetEntity networked = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            other = entities.SpawnEntity("MobHuman", map.GridCoords);
            foreach (var uid in new[] { user, other })
            {
                var feature = BodyCustomizationCatalog.Default("penis");
                feature.Enabled = true; feature.Size = "2";
                entities.GetComponent<HumanoidAppearanceComponent>(uid).BodyFeatures = [feature];
            }
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), user);
            networked = entities.GetNetEntity(user);
        });
        await pair.RunTicksSync(30);
        foreach (var (arguments, expected) in new[] { ($"1 {networked}", false), ("bad", false), ("1", true), ("1", true), ("0", false), ("", true) })
        {
            await pair.Server.WaitAssertion(() =>
            {
                var console = pair.Server.ResolveDependency<IConsoleHost>();
                if (arguments == "bad" || arguments.Contains(' '))
                {
                    var shell = new Mock<IConsoleShell>();
                    shell.SetupGet(s => s.Player).Returns(pair.Server.PlayerMan.Sessions.Single());
                    new SetGenderArousalCommand().Execute(shell.Object, arguments, arguments.Split(' '));
                    shell.Verify(s => s.WriteError(It.IsAny<string>()), Times.Once);
                }
                else console.ExecuteCommand(pair.Server.PlayerMan.Sessions.Single(), "set_gender_arousal " + arguments);
                Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).GenitalAroused, Is.EqualTo(expected));
                Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(other).GenitalAroused, Is.False);
            });
            await pair.RunTicksSync(20);
            await pair.Client.WaitAssertion(() =>
            {
                var entities = pair.Client.EntMan;
                var uid = entities.GetEntity(networked);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                Assert.That(humanoid.GenitalAroused, Is.EqualTo(expected));
                Assert.That(humanoid.BodyFeatures.Single().Size, Is.EqualTo("2"));
                var sprite = entities.GetComponent<SpriteComponent>(uid);
                var layer = (SpriteComponent.Layer) sprite[sprite.LayerMapGet("dl-body/penis/FRONT")];
                Assert.That(layer.ActualState!.StateId.ToString(), Is.EqualTo($"penis_human_2_{(expected ? 1 : 0)}_FRONT"));
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            var shell = new Mock<IConsoleShell>();
            new SetGenderArousalCommand().Execute(shell.Object, "0", ["0"]);
            shell.Verify(s => s.WriteError(It.IsAny<string>()), Times.Once);
            Assert.That(pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).GenitalAroused, Is.True);
            pair.Server.EntMan.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures.Clear();
            Assert.That(pair.Server.EntMan.System<BodyCustomizationSystem>().SetGenitalArousal(user, false), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
        });
        await pair.CleanReturnAsync();
    }

    private static readonly string[] Reagents = ["DLButtGrowth", "DLButtReduction", "DLBellyGrowth", "DLBellyReduction",
        "DLBreastsGrowth", "DLBreastsReduction", "DLPenisGrowth", "DLPenisReduction"];

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
            Assert.That(system.SetVisibility(uid, "penis", 1), Is.False);
            entities.DeleteEntity(uid);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MissingFeaturesRequireGrowthOverdoseAndReductionNeverCreatesThem()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            foreach (var id in Reagents)
            foreach (var disabled in new[] { false, true })
            {
                var uid = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(uid);
                var reagent = prototypes.Index<ReagentPrototype>(id);
                var metabolism = reagent.Metabolisms!.Single().Value;
                Assert.That(metabolism.MetabolismRate, Is.EqualTo(FixedPoint2.New(0.25f)));
                var effect = (ChangeBodyFeatureSize) metabolism.Effects.Single();
                var original = BodyCustomizationCatalog.Default(effect.Part);
                humanoid.BodyFeatures = disabled ? [original] : [];
                var low = new Solution(); low.AddReagent(id, FixedPoint2.New(effect.CreationThreshold - 0.25f));
                effect.Effect(new EntityEffectReagentArgs(uid, entities, null, low, 5, reagent, null, 1));
                Assert.That(humanoid.BodyFeatures.Any(f => f.Enabled), Is.False, "A normal dose cannot create a feature");
                var overdose = new Solution(); overdose.AddReagent(id, FixedPoint2.New(effect.CreationThreshold));
                effect.Effect(new EntityEffectReagentArgs(uid, entities, null, overdose, FixedPoint2.New(0.25f), reagent, null, 1));
                Assert.That(humanoid.BodyFeatures.Any(f => f.Enabled), Is.EqualTo(effect.Direction > 0));
                Assert.That(original.Enabled, Is.False, "Saved features must not be mutated");
                if (effect.Direction > 0)
                {
                    var sizes = BodyCustomizationCatalog.Sizes(effect.Part, humanoid.BodyFeatures.Single().Shape);
                    Assert.That(humanoid.BodyFeatures.Single().Size, Is.EqualTo(sizes.First()));
                    effect.Effect(new EntityEffectReagentArgs(uid, entities, null, overdose, FixedPoint2.New(4.75f), reagent, null, 1));
                    Assert.That(humanoid.BodyFeatures.Single().Size, Is.EqualTo(sizes[1]));
                }
                entities.DeleteEntity(uid);
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ChemicalPreferenceBlocksDosesAndCreationAndClearsPartialProgress()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid user = default;
        EntityUid mind = default;
        await pair.Server.WaitPost(() =>
        {
            var entities = pair.Server.EntMan;
            user = entities.SpawnEntity("MobHuman", map.GridCoords);
            var minds = entities.System<Content.Server.Mind.MindSystem>();
            mind = minds.GetOrCreateMind(pair.Server.PlayerMan.Sessions.Single().UserId).Owner;
            minds.TransferTo(mind, user);
            var feature = BodyCustomizationCatalog.Default("breasts");
            feature.Enabled = true;
            entities.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures = [feature];
            Assert.That(entities.System<BodyCustomizationSystem>().ApplyDose(user, "breasts", 1, 4), Is.False);
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(BodyCustomizationCVars.ChemicalChanges, false));
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            using var tab = new Content.Client._DV.Options.UI.Tabs.DeltaTab();
            var checkBox = tab.FindControl<Robust.Client.UserInterface.Controls.CheckBox>("BodyChemicalChangesCheckBox");
            Assert.That(checkBox.Text, Is.EqualTo(Loc.GetString("ui-options-body-chemical-changes")));
            Assert.That(checkBox.Pressed, Is.False);
            checkBox.Pressed = true;
            var toggled = (Action<Robust.Client.UserInterface.Controls.BaseButton.ButtonToggledEventArgs>)
                typeof(Robust.Client.UserInterface.Controls.BaseButton).GetField("OnToggled", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(checkBox)!;
            toggled(new Robust.Client.UserInterface.Controls.BaseButton.ButtonToggledEventArgs(true, checkBox, null!));
            Assert.That(tab.FindControl<Robust.Client.UserInterface.Controls.Button>("ApplyButton").Disabled, Is.False);
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<BodyCustomizationSystem>();
            var before = entities.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures.Select(f => f.Clone()).ToList();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            foreach (var id in Reagents)
            {
                var reagent = prototypes.Index<ReagentPrototype>(id);
                var effect = (ChangeBodyFeatureSize) reagent.Metabolisms!.Single().Value.Effects.Single();
                Assert.That(system.ApplyDose(user, effect.Part, effect.Direction, 100), Is.False);
            }
            Assert.That(BodyCustomizationCatalog.ContentEquals(before,
                entities.GetComponent<HumanoidAppearanceComponent>(user).BodyFeatures), Is.True);
            Assert.That(entities.GetComponent<BodySizeDoseComponent>(user).Doses, Is.Empty);
            entities.System<Content.Server.Mind.MindSystem>().Visit(mind, entities.SpawnEntity("MobObserver", map.GridCoords));
            Assert.That(system.ApplyDose(user, "penis", 1, 5), Is.False, "The owner's preference also protects their body while ghosting");
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(BodyCustomizationCVars.ChemicalChanges, true));
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<BodyCustomizationSystem>();
            Assert.That(system.ApplyDose(user, "breasts", 1, 1), Is.False, "Old partial doses must be discarded");
            Assert.That(system.ApplyDose(user, "breasts", 1, 4), Is.True);
            Assert.That(system.ApplyDose(user, "penis", 1, 5), Is.True);
            entities.System<Content.Server.Mind.MindSystem>().UnVisit(mind);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
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
