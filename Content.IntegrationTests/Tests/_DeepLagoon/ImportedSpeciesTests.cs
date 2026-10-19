using System;
using System.Linq;
using Content.Server.Actions;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared._DeepLagoon.Species.Ariral;
using Content.Shared._DeepLagoon.Species.Arkana;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.Dataset;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class ImportedSpeciesTests
{
    [TestCase("Ariral", "AriralBlood", "OrganAriralStomach")]
    [TestCase("Demon", "Blood", "OrganDemonStomach")]
    [TestCase("Vox", "AmmoniaBlood", "OrganVoxStomach")]
    public async Task SelectableSpeciesSpawnWithTheirOwnBodyAndOrgans(string species, string blood, string stomach)
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var definition = prototypes.Index<SpeciesPrototype>(species);
            Assert.That(definition.RoundStart, Is.True);
            foreach (var dataset in new[] { definition.MaleFirstNames, definition.FemaleFirstNames, definition.LastNames })
                Assert.That(prototypes.Index<DatasetPrototype>(dataset).Values, Is.Not.Empty, "Name generation must use native datasets");
            var mob = entities.SpawnEntity(definition.Prototype, map.GridCoords);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(mob, HumanoidCharacterProfile.DefaultWithSpecies(species));
            var body = entities.GetComponent<BodyComponent>(mob);
            Assert.That(body.Prototype?.Id, Is.EqualTo(species));
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(mob).Species.Id, Is.EqualTo(species));
            Assert.That(entities.GetComponent<BloodstreamComponent>(mob).BloodReagent.Id, Is.EqualTo(blood));
            var bodies = entities.System<BodySystem>();
            Assert.That(bodies.GetBodyChildrenOfType(mob, BodyPartType.Leg, body).Count(), Is.EqualTo(2));
            Assert.That(bodies.GetBodyOrganEntityComps<StomachComponent>((mob, body))
                .Any(organ => entities.GetComponent<MetaDataComponent>(organ.Owner).EntityPrototype?.ID == stomach), Is.True);
            if (species == "Ariral")
            {
                var layer = entities.GetComponent<EggAriralLayerComponent>(mob);
                Assert.That(layer.Action, Is.Not.Null, "Arirals must receive their egg action at map init");
                var actions = entities.System<ActionsSystem>();
                Assert.That(actions.TryGetActionData(layer.Action, out var action), Is.True);
                Assert.That(actions.GetCharges(layer.Action), Is.EqualTo(1));
                var eggsBefore = entities.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == "FoodAriralEggLaid");
                actions.PerformAction(mob, null, layer.Action.Value, action, action.BaseEvent, TimeSpan.Zero);
                Assert.That(actions.GetCharges(layer.Action), Is.Zero);
                Assert.That(actions.ValidAction(action), Is.False);
                Assert.That(entities.EntityQuery<MetaDataComponent>().Count(meta => meta.EntityPrototype?.ID == "FoodAriralEggLaid"), Is.EqualTo(eggsBefore + 1));
            }
            var dummy = entities.SpawnEntity(definition.DollPrototype, map.GridCoords);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(dummy, HumanoidCharacterProfile.DefaultWithSpecies(species));
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(dummy).Species.Id, Is.EqualTo(species));
            entities.DeleteEntity(dummy);
            entities.DeleteEntity(mob);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ArkanaFeedingRequiresStomachCapacityAndRejectsAriralBlood()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var solutions = entities.System<SharedSolutionContainerSystem>();
            var demon = entities.SpawnEntity("MobDemon", map.GridCoords);
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var ariral = entities.SpawnEntity("MobAriral", map.GridCoords);
            var body = entities.GetComponent<BodyComponent>(demon);
            var stomach = entities.System<BodySystem>().GetBodyOrganEntityComps<StomachComponent>((demon, body)).Single();
            Assert.That(solutions.TryGetSolution(stomach.Owner, "stomach", out var stomachEntity, out var contents), Is.True);
            Assert.That(solutions.TryGetSolution(human, "bloodstream", out _, out var humanBlood), Is.True);
            Assert.That(solutions.TryGetSolution(ariral, "bloodstream", out _, out var ariralBlood), Is.True);
            var humanBefore = humanBlood.Volume;
            var ariralBefore = ariralBlood.Volume;
            solutions.TryAddReagent(stomachEntity.Value, "Water", contents.AvailableVolume, out _);
            Feed(human);
            Assert.That(humanBlood.Volume, Is.EqualTo(humanBefore), "A full stomach must not consume victim blood");
            solutions.RemoveAllSolution(stomachEntity.Value);
            Feed(ariral);
            Assert.That(ariralBlood.Volume, Is.EqualTo(ariralBefore));
            Assert.That(contents.Volume, Is.EqualTo(FixedPoint2.Zero));
            Feed(human);
            Assert.That(humanBlood.Volume, Is.EqualTo(humanBefore - FixedPoint2.New(25)));
            Assert.That(contents.Volume, Is.EqualTo(FixedPoint2.New(25)));
            entities.DeleteEntity(ariral);
            entities.DeleteEntity(human);
            entities.DeleteEntity(demon);

            void Feed(EntityUid target)
            {
                var ev = new ArkanaBloodDrinkDoAfterEvent();
                ev.DoAfter = new Content.Shared.DoAfter.DoAfter(0, new DoAfterArgs(entities, demon, TimeSpan.Zero, ev, demon, target), TimeSpan.Zero);
                entities.EventBus.RaiseLocalEvent(demon, ev);
            }
        });
        await pair.CleanReturnAsync();
    }
}
