using Content.Server.Abilities.Psionics;
using Content.Server.Chemistry.ReagentEffects;
using Content.Server.GameTicking;
using Content.Server.Mood;
using Content.Server.Psionics;
using Content.Server.Traits;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mood;
using Content.Shared.Preferences;
using Content.Shared.Psionics;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Linq;
using Robust.Shared.Configuration;
using Content.Shared.CCVar;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PsionicsMoodTests
{
    [Test]
    public async Task PsionicRecipesProduceOilCrystalsAndDust()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        Content.Shared.Chemistry.Components.Solution oil = null!;
        Content.Shared.Chemistry.Components.Solution crystal = null!;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var solutions = entities.System<Content.Shared.Chemistry.EntitySystems.SharedSolutionContainerSystem>();
            var beaker = entities.SpawnEntity("Beaker", map.GridCoords);
            Assert.That(solutions.TryGetSolution(beaker, "beaker", out var oilEntity, out oil), Is.True);
            solutions.TryAddReagent(oilEntity!.Value, "SpaceDrugs", Content.Shared.FixedPoint.FixedPoint2.New(10));
            solutions.TryAddReagent(oilEntity.Value, "Cognizine", Content.Shared.FixedPoint.FixedPoint2.New(10));
            solutions.TryAddReagent(oilEntity.Value, "Plasma", Content.Shared.FixedPoint.FixedPoint2.New(5));
            var crystalBeaker = entities.SpawnEntity("Beaker", map.GridCoords);
            Assert.That(solutions.TryGetSolution(crystalBeaker, "beaker", out var crystalEntity, out crystal), Is.True);
            foreach (var id in new[] { "Ash", "Water", "Blood", "Ectoplasm", "Plasma" })
                solutions.TryAddReagent(crystalEntity!.Value, id, Content.Shared.FixedPoint.FixedPoint2.New(5));
            solutions.SetTemperature(crystalEntity!.Value, 400f);
            solutions.UpdateChemicals(crystalEntity.Value);
            var bowl = entities.SpawnEntity("FoodOusianaDustBowl", map.GridCoords);
            Assert.That(solutions.TryGetSolution(bowl, "food", out _, out var dust), Is.True);
            Assert.That(dust!.GetTotalPrototypeQuantity("OusianaDust"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.New(5)));
        });
        await pair.Server.WaitRunTicks(2);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(oil.GetTotalPrototypeQuantity("LotophagoiOil"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.New(10)));
            Assert.That(oil.GetTotalPrototypeQuantity("Plasma"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.New(5)), "Catalyst must not be consumed");
            Assert.That(oil.GetTotalPrototypeQuantity("SpaceDrugs", "Cognizine"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.Zero));
            Assert.That(crystal.GetTotalPrototypeQuantity("Plasma"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.New(5)));
            Assert.That(crystal.GetTotalPrototypeQuantity("Ash", "Water", "Blood", "Ectoplasm"), Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.Zero));
            var query = pair.Server.EntMan.EntityQueryEnumerator<MetaDataComponent>();
            var found = false;
            while (query.MoveNext(out _, out var metadata))
                found |= metadata.EntityPrototype?.ID == "MaterialNormality1";
            Assert.That(found, Is.True, "Heating the recipe must produce a normality crystal");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundstartAndReagentsRequirePsionicTraits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var system = entities.System<PsionicsSystem>();
            Assert.That(config.GetCVar(CCVars.PsionicRoundstartChance), Is.EqualTo(0.012f));
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var psionic = entities.GetComponent<PsionicComponent>(human);
            var other = entities.SpawnEntity("MobHuman", map.GridCoords);
            var otherPsionic = entities.GetComponent<PsionicComponent>(other);
            config.SetCVar(CCVars.PsionicRoundstartChance, 1f);
            system.RollRoundstartPsionics(other, otherPsionic);
            Assert.That(otherPsionic.ActivePowers, Is.Empty);

            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("LatentPsychic", prototypes);
            entities.System<TraitSystem>().ApplyTraits(human, "Sheriff", profile);
            config.SetCVar(CCVars.PsionicRoundstartChance, 0f);
            system.RollRoundstartPsionics(human, psionic);
            Assert.That(psionic.ActivePowers, Is.Empty);
            Assert.That(psionic.Potentia, Is.Zero);
            Assert.That(psionic.CanReroll, Is.True);
            config.SetCVar(CCVars.PsionicRoundstartChance, 1f);
            config.SetCVar(CCVars.PsionicRollsEnabled, false);
            system.RollRoundstartPsionics(human, psionic);
            Assert.That(psionic.ActivePowers, Is.Empty);
            config.SetCVar(CCVars.PsionicRollsEnabled, true);
            system.RollRoundstartPsionics(human, psionic);
            Assert.That(psionic.ActivePowers.Count, Is.EqualTo(1));
            Assert.That(psionic.NextPowerCost, Is.EqualTo(psionic.BaselinePowerCost * MathF.Pow(2, psionic.PowerSlotsTaken)));
            Assert.That(psionic.Potentia, Is.Zero);
            Assert.That(psionic.CanReroll, Is.True);
            config.SetCVar(CCVars.PsionicRoundstartChance, 0f);
            otherPsionic.Potentia = otherPsionic.NextPowerCost;
            var oil = prototypes.Index<ReagentPrototype>("LotophagoiOil");
            var effect = oil.Metabolisms!["Narcotic"].Effects.OfType<ChemRerollPsionic>().Single();
            effect.Effect(new EntityEffectReagentArgs(other, entities, null, null, 20, oil, null, 1));
            Assert.That(otherPsionic.ActivePowers, Is.Empty, "A baseline Psionic component is not a character trait");
            Assert.That(otherPsionic.CanReroll, Is.True, "Rejected chemistry must not spend potential");
            Assert.That(otherPsionic.Potentia, Is.EqualTo(otherPsionic.NextPowerCost));
            entities.System<TraitSystem>().ApplyTraits(other, "Sheriff", profile);
            effect.Effect(new EntityEffectReagentArgs(other, entities, null, null, 20, oil, null, 1));
            Assert.That(otherPsionic.ActivePowers, Is.Not.Empty, "A character with the trait can still awaken chemically");
            Assert.That(otherPsionic.CanReroll, Is.False);
            var familiar = entities.SpawnEntity("MobBatRemilia", map.GridCoords);
            Assert.That(entities.GetComponent<PsionicComponent>(familiar).ActivePowers, Is.Not.Empty, "Innate powers must remain available");
            entities.DeleteEntity(familiar);
            entities.DeleteEntity(other);
            entities.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase("NoosphericStorm")]
    [TestCase("NoosphericZap")]
    public async Task NoosphericEventsAwakenOnlyLatentPsychics(string ruleId)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var latent = entities.SpawnEntity("MobHuman", map.GridCoords);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("LatentPsychic", prototypes);
            entities.System<TraitSystem>().ApplyTraits(latent, "Sheriff", profile);
            var humanPsionic = entities.GetComponent<PsionicComponent>(human);
            var latentPsionic = entities.GetComponent<PsionicComponent>(latent);
            humanPsionic.Potentia = humanPsionic.NextPowerCost;
            latentPsionic.Potentia = latentPsionic.NextPowerCost;

            var ticker = entities.System<GameTicker>();
            var rule = ticker.AddGameRule(ruleId);
            entities.GetComponent<GameRuleComponent>(rule).Delay = null;
            Assert.That(ticker.StartGameRule(rule), Is.True);
            Assert.That(humanPsionic.ActivePowers, Is.Empty);
            Assert.That(latentPsionic.ActivePowers, Has.Count.EqualTo(1));
            ticker.EndGameRule(rule);
            entities.DeleteEntity(human);
            entities.DeleteEntity(latent);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NoosphericZapCreatesLightningAndParalyzesTarget()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var caster = entities.SpawnEntity("MobHuman", map.GridCoords);
            var target = entities.SpawnEntity("MobHuman", map.GridCoords.Offset(new System.Numerics.Vector2(2, 0)));
            var power = pair.Server.ResolveDependency<IPrototypeManager>().Index<PsionicPowerPrototype>("NoosphericZapPower");
            entities.System<PsionicAbilitiesSystem>().InitializePsionicPower(caster, power, false);
            var action = new Content.Shared.Actions.Events.NoosphericZapPowerActionEvent { Performer = caster, Target = target };
            entities.EventBus.RaiseLocalEvent(caster, action, broadcast: true);
            Assert.That(action.Handled, Is.True);
            var statuses = entities.System<Content.Shared.StatusEffect.StatusEffectsSystem>();
            Assert.That(statuses.HasStatusEffect(target, "Stun"), Is.True);
            Assert.That(statuses.HasStatusEffect(target, "KnockedDown"), Is.True);
            Assert.That(statuses.HasStatusEffect(target, "Stutter"), Is.True);
            var beams = entities.EntityQueryEnumerator<Content.Server.Beam.Components.BeamComponent, MetaDataComponent>();
            var found = false;
            while (beams.MoveNext(out _, out _, out var metadata))
                found |= metadata.EntityPrototype?.ID == "LightningNoospheric";
            Assert.That(found, Is.True, "Casting must create the noospheric lightning beam");
            entities.DeleteEntity(caster);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task PsionicObjectsAndAllPowerInitializersWork()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            foreach (var id in new[] { "GlimmerProber", "GlimmerDrain", "GlimmerDeviceFrame",
                         "ComputerPsionicsRecords", "GlimmerMonitorCartridge", "MobGlimmerWisp", "MobGlimmerMite",
                         "MobPsionicFamiliarImp", "MobIfritFamiliar", "MobTelegnosisObserver", "ShadowkinShadow",
                         "AnomalyPyroclastic", "AnomalyGravity", "AnomalyElectricity", "AnomalyFlesh",
                         "AnomalyBluespace", "AnomalyIce", "AnomalyFlora", "AnomalyLiquid", "AnomalyShadow",
                         "AnomalyTech", "AnomalySanta", "MobQuartzCrab", "ReagentSlime", "MobXeno", "MobRevenant",
                         "MobBatRemilia", "MobCorgiCerberus", "MobIfritGuardian", "MobIPC" })
            {
                var uid = entities.SpawnEntity(id, map.GridCoords);
                entities.DeleteEntity(uid);
            }
            var abilities = entities.System<PsionicAbilitiesSystem>();
            foreach (var power in pair.Server.ResolveDependency<IPrototypeManager>().EnumeratePrototypes<PsionicPowerPrototype>())
            {
                var human = entities.SpawnEntity("MobHuman", map.GridCoords);
                abilities.InitializePsionicPower(human, power, false);
                var psionic = entities.GetComponent<PsionicComponent>(human);
                Assert.That(psionic.ActivePowers, Does.Contain(power), power.ID);
                abilities.RemoveAllPsionicPowers(human);
                Assert.That(psionic.ActivePowers, Is.Empty, power.ID);
                Assert.That(psionic.Actions, Is.Empty, power.ID);
                entities.DeleteEntity(human);
            }
        });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var factory = pair.Client.ResolveDependency<IComponentFactory>();
            foreach (var (id, state) in new[] { ("MobGlimmerWisp", "willowisp"), ("MobGlimmerMite", "mite"),
                         ("GlimmerProber", "prober"), ("ComputerPsionicsRecords", "registry") })
            {
                var prototype = prototypes.Index<EntityPrototype>(id);
                Assert.That(prototype.TryComp<Robust.Client.GameObjects.SpriteComponent>(out var sprite, factory), Is.True, id);
                Assert.That(sprite.BaseRSI.TryGetState(state, out _), Is.True, $"{id}: {state}");
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ImportedPrototypesValidate()
    {
        await using var pair = await PoolManager.GetServerClient();
        // Server validation includes chemistry and ability handlers that intentionally do not exist on the client.
        await pair.Server.WaitAssertion(() =>
        {
            var errors = pair.Server.ResolveDependency<IPrototypeManager>()
                .ValidateDirectory(new ResPath("/Prototypes"))
                .Where(file => file.Key.Contains("/_DL/Psionics/"))
                .ToDictionary(file => file.Key, file => file.Value);
            Assert.That(errors, Is.Empty, string.Join("\n", errors.SelectMany(file =>
                file.Value.Select(error => $"{file.Key}: {error.ErrorReason}"))));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MoodReplacesNeedsAndSynchronizesLevel()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var mood = entities.GetComponent<MoodComponent>(human);
            var neutral = mood.CurrentMoodLevel;
            entities.EventBus.RaiseLocalEvent(human, new MoodEffectEvent("PetAnimal"));
            Assert.That(mood.CurrentMoodLevel, Is.EqualTo(neutral + 3));
            entities.EventBus.RaiseLocalEvent(human, new MoodEffectEvent("PetAnimal"));
            Assert.That(mood.CurrentMoodLevel, Is.EqualTo(neutral + 3), "Repeated moodlets must refresh without stacking");
            entities.EventBus.RaiseLocalEvent(human, new MoodRemoveEffectEvent("PetAnimal"));
            Assert.That(mood.CurrentMoodLevel, Is.EqualTo(neutral));
            entities.EventBus.RaiseLocalEvent(human, new MoodEffectEvent("HealthHeavyDamage"));
            entities.EventBus.RaiseLocalEvent(human, new MoodEffectEvent("HealthNoDamage"));
            Assert.That(mood.CurrentMoodLevel, Is.EqualTo(neutral), "Recovery must replace the injury moodlet");
            Assert.That(entities.GetComponent<NetMoodComponent>(human).CurrentMoodLevel, Is.EqualTo(mood.CurrentMoodLevel));
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            config.SetCVar(CCVars.MoodEnabled, false);
            Assert.That(entities.HasComponent<NetMoodComponent>(human), Is.False);
            config.SetCVar(CCVars.MoodEnabled, true);
            Assert.That(entities.GetComponent<NetMoodComponent>(human).CurrentMoodLevel, Is.EqualTo(neutral));
            entities.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemovingAndRegrantingPowerRestoresActions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var abilities = entities.System<PsionicAbilitiesSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var power = prototypes.Index<PsionicPowerPrototype>("NoosphericZapPower");
            abilities.InitializePsionicPower(human, power, false);
            var psionic = entities.GetComponent<PsionicComponent>(human);
            Assert.That(psionic.ActivePowers, Does.Contain(power));
            Assert.That(psionic.Actions.Count, Is.GreaterThan(0));
            var casting = entities.System<SharedPsionicAbilitiesSystem>();
            entities.AddComponent<PsionicsDisabledComponent>(human);
            Assert.That(casting.OnAttemptPowerUse(human, "Test"), Is.False);
            entities.RemoveComponent<PsionicsDisabledComponent>(human);
            abilities.RemoveAllPsionicPowers(human);
            Assert.That(psionic.ActivePowers, Is.Empty);
            Assert.That(psionic.Actions, Is.Empty);
            Assert.That(psionic.PowerSlotsTaken, Is.Zero);
            abilities.InitializePsionicPower(human, power, false);
            Assert.That(psionic.ActivePowers, Does.Contain(power));
            Assert.That(psionic.Actions.Count, Is.GreaterThan(0));
            entities.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EquipmentGrantsActionsAndPreservesInnatePowers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var gloves = entities.SpawnEntity("ClothingHandsGlovesColorYellowUnsulated", map.GridCoords);
            var inventory = entities.System<InventorySystem>();
            var power = pair.Server.ResolveDependency<IPrototypeManager>().Index<PsionicPowerPrototype>("NoosphericZapPower");
            var psionic = entities.GetComponent<PsionicComponent>(human);
            Assert.That(inventory.TryEquip(human, gloves, "gloves", force: true), Is.True);
            Assert.That(psionic.ActivePowers, Does.Contain(power));
            Assert.That(psionic.Actions.Count, Is.GreaterThan(0));
            Assert.That(inventory.TryUnequip(human, "gloves", force: true), Is.True);
            Assert.That(psionic.ActivePowers, Does.Not.Contain(power));
            Assert.That(psionic.Actions, Is.Empty);
            entities.System<PsionicAbilitiesSystem>().InitializePsionicPower(human, power, false);
            Assert.That(inventory.TryEquip(human, gloves, "gloves", force: true), Is.True);
            Assert.That(inventory.TryUnequip(human, "gloves", force: true), Is.True);
            Assert.That(psionic.ActivePowers, Does.Contain(power), "Unequipping must preserve a power acquired independently of equipment");
            entities.DeleteEntity(human);
            entities.DeleteEntity(gloves);
        });
        await pair.CleanReturnAsync();
    }
}
