using System.Linq;
using Content.Server.Abilities.Psionics;
using Content.Server.Chemistry.ReagentEffects;
using Content.Server.Psionics;
using Content.Server.StationEvents;
using Content.Server.Traits;
using Content.Shared.Abilities.Psionics;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.EntityTable;
using Content.Shared.Preferences;
using Content.Shared.Psionics;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PsionicsAccessTests
{
    [TestCase("LatentPsychic")]
    [TestCase("PsychoHistorian")]
    public async Task ChemicalRestorationRequiresAnAppliedPsionicTrait(string trait)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var target = entities.SpawnEntity("MobHuman", map.GridCoords);
            var psionic = entities.GetComponent<PsionicComponent>(target);
            psionic.CanReroll = false;
            var reagent = prototypes.Index<ReagentPrototype>("OusianaDust");
            var effect = reagent.Metabolisms!["Narcotic"].Effects.OfType<ChemRestorePsionicReroll>().Single();
            effect.Effect(new EntityEffectReagentArgs(target, entities, null, null, 5, reagent, null, 1));
            Assert.That(psionic.CanReroll, Is.False);

            // Even a power supplied by equipment, a job or an administrator is not a selected trait.
            entities.System<PsionicAbilitiesSystem>().InitializePsionicPower(target,
                prototypes.Index<PsionicPowerPrototype>("TelepathyPower"), false);
            effect.Effect(new EntityEffectReagentArgs(target, entities, null, null, 5, reagent, null, 1));
            Assert.That(psionic.CanReroll, Is.False);
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference(trait, prototypes);
            entities.System<TraitSystem>().ApplyTraits(target, "Sheriff", profile);
            Assert.That(entities.HasComponent<PsionicTraitComponent>(target), Is.True);
            effect.Effect(new EntityEffectReagentArgs(target, entities, null, null, 5, reagent, null, 1));
            Assert.That(psionic.CanReroll, Is.True);
            entities.DeleteEntity(target);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NoosphericZapIsUnavailableToAutomaticSchedulers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.GlimmerEnabled, true);
            entities.System<Content.Shared.Psionics.Glimmer.GlimmerSystem>().SetGlimmerOutput(500);
            var events = entities.System<EventManagerSystem>();
            Assert.That(events.AvailableEvents(false, 100, TimeSpan.FromHours(4)).Keys.Select(p => p.ID),
                Does.Not.Contain("NoosphericZap"));
            var table = prototypes.Index<EntityTablePrototype>("PsionicGlimmerEventsTable");
            Assert.That(entities.System<EntityTableSystem>().GetSpawns(table.Table).Select(p => p.Id),
                Does.Not.Contain("NoosphericZap"));
            Assert.That(prototypes.HasIndex<PsionicPowerPrototype>("NoosphericZapPower"), Is.True,
                "Disabling the random event must not remove the active power");
        });
        await pair.CleanReturnAsync();
    }
}
