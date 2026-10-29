using System.Collections.Generic;
using System.Linq;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BlueMoonPhysiologyTests
{
    private static BodyFeature Feature(string part)
    {
        var feature = BodyCustomizationCatalog.Default(part);
        feature.Enabled = true;
        return feature;
    }

    [Test]
    public async Task ReferenceCycleConservesFluidsAndHonorsGrowthAndPregnancySettings()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid source = default, recipient = default, vessel = default;
        string originalBelly = "";
        string pregnancyBaseline = "";
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            source = entities.SpawnEntity("MobHuman", map.GridCoords);
            recipient = entities.SpawnEntity("MobHuman", map.GridCoords);
            entities.GetComponent<HumanoidAppearanceComponent>(source).BodyFeatures = [Feature("penis"), Feature("testicles")];
            entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures = [Feature("vagina"), Feature("womb"), Feature("belly")];
            var sourcePrefs = new BlueMoonPreferences { Potency = -1, Virility = 100 };
            entities.GetComponent<InteractionPanelPreferencesComponent>(source).Profile = new HumanoidCharacterProfile().WithArousalPreferences(sourcePrefs);
            entities.GetComponent<InteractionPanelPreferencesComponent>(recipient).Profile = new HumanoidCharacterProfile()
                .WithArousalPreferences(new BlueMoonPreferences { ReceiveFluids = true });
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), recipient);
            var system = entities.System<InteractionPanelArousalSystem>();
            var state = entities.GetComponent<InteractionPanelArousalComponent>(source);
            Assert.That(system.Maximum(source), Is.EqualTo(300));
            var sourceProfile = entities.GetComponent<InteractionPanelPreferencesComponent>(source);
            sourceProfile.Profile = sourceProfile.Profile.WithArousalPreferences(sourcePrefs with { Penis = BlueMoonSensitivity.Hypersensitive });
            system.AdjustArousal(source, source, 12, bodyPart: "penis");
            Assert.That(state.Current, Is.EqualTo(36), "The selected organ applies its own sensitivity");
            sourceProfile.Profile = sourceProfile.Profile.WithArousalPreferences(sourcePrefs);

            state.Current = 299;
            entities.EnsureComponent<BlueMoonPhysiologyComponent>(source).LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            system.AdjustArousal(source, source, 1);
            Assert.That(state.Current, Is.EqualTo(300), "Exact threshold does not climax in BlueMoon");
            system.AdjustArousal(source, source, 1);
            Assert.That(state.Current, Is.Zero);
            Assert.That(state.Orgasms, Is.EqualTo(2));
            var seconds = (state.RecoveryUntil - pair.Server.ResolveDependency<IGameTiming>().CurTime).TotalSeconds;
            Assert.That(seconds, Is.InRange(30, 90));
            system.AdjustArousal(source, source, 6);
            Assert.That(state.Current, Is.EqualTo(6), "Recovery does not globally block stimulation");
            var physiology = entities.GetComponent<BlueMoonPhysiologyComponent>(source);
            state.Current = 0;
            for (var i = 0; i < 30; i++)
            {
                physiology.NextChemicalEffect.Clear();
                system.ApplyBlueMoonChemical(source, "Crocin", 5);
            }
            Assert.That(state.Current, Is.EqualTo(96), "12-point doses stop once the 30 percent check fails");
            state.Current = 100;
            physiology.NextChemicalEffect.Clear();
            system.ApplyBlueMoonChemical(source, "Camphor", 5);
            Assert.That(state.Current, Is.EqualTo(80));
            physiology.NextChemicalEffect.Clear();
            system.ApplyBlueMoonChemical(source, "Hexacamphor", 5);
            Assert.That(state.Current, Is.EqualTo(60));
            state.Current = 0;
            for (var i = 0; i < 30; i++)
            {
                physiology.NextChemicalEffect.Clear();
                system.ApplyBlueMoonChemical(source, "Hexacrocin", 5);
            }
            Assert.That(state.Current, Is.EqualTo(288), "Hexacrocin stops after crossing the 95 percent check");
            Assert.That(system.TryStartPregnancy(source, recipient), Is.False);
            originalBelly = entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures.Single(f => f.Part == "belly").Size;
            physiology.LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            Assert.That(system.AvailableFluid(source, "penis"), Is.EqualTo(50));
            Assert.That(system.TryClimax(source, recipient, "penis", "belly", allParts: false), Is.True);
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetSolution(recipient, "bluemoon-internal-belly", out _, out var contents), Is.True);
            Assert.That(contents!.Volume, Is.EqualTo(FixedPoint2.New(50)));
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures.Single(f => f.Part == "belly").Size, Is.EqualTo(originalBelly));
            system.TryClimax(source, recipient, "penis", "belly", allParts: false);
            Assert.That(contents.Volume, Is.EqualTo(FixedPoint2.New(50)), "A second emission at the same time cannot duplicate fluids");
            var preferences = entities.GetComponent<InteractionPanelPreferencesComponent>(recipient);
            preferences.Profile = preferences.Profile.WithArousalPreferences(preferences.Profile.ArousalPreferences with { FluidGrowth = true });
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(BodyCustomizationCVars.ChemicalChanges, false));
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<InteractionPanelArousalSystem>();
            entities.GetComponent<BlueMoonPhysiologyComponent>(source).LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            system.TryClimax(source, recipient, "penis", "belly", allParts: false);
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures.Single(f => f.Part == "belly").Size, Is.EqualTo(originalBelly),
                "The global body-change setting also blocks fluid growth");
        });
        await pair.Client.WaitPost(() => pair.Client.CfgMan.SetCVar(BodyCustomizationCVars.ChemicalChanges, true));
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<InteractionPanelArousalSystem>();
            var physiology = entities.GetComponent<BlueMoonPhysiologyComponent>(source);
            physiology.LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            system.TryClimax(source, recipient, "penis", "belly", allParts: false);
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures.Single(f => f.Part == "belly").Size, Is.Not.EqualTo(originalBelly));
            var prefs = entities.GetComponent<InteractionPanelPreferencesComponent>(recipient);
            prefs.Profile = prefs.Profile.WithArousalPreferences(prefs.Profile.ArousalPreferences with { Pregnancy = true, Fertility = 100 });

            for (var i = 0; i < 100 && !entities.GetComponent<BlueMoonPhysiologyComponent>(recipient).Pregnant; i++)
            {
                physiology.LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(5);
                system.TryClimax(source, recipient, "penis", "vagina", allParts: false);
            }
            Assert.That(entities.GetComponent<BlueMoonPhysiologyComponent>(recipient).Pregnant, Is.True);
            var pregnancy = entities.GetComponent<BlueMoonPhysiologyComponent>(recipient);
            pregnancy.PregnancyStarted = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromMinutes(46);
            vessel = entities.SpawnEntity("Beaker", map.GridCoords);
            var solutions = entities.System<SharedSolutionContainerSystem>();
            Assert.That(solutions.TryGetRefillableSolution(vessel, out _, out var vesselContents), Is.True);
            physiology.LastEmission["penis"] = pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(250);
            system.TryClimax(source, bodyPart: "penis", container: vessel, allParts: false);
            Assert.That(vesselContents!.GetTotalPrototypeQuantity("DLSemen"), Is.EqualTo(FixedPoint2.New(50)));
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var pregnancy = entities.GetComponent<BlueMoonPhysiologyComponent>(recipient);
            Assert.That(pregnancy.PregnancyStage, Is.EqualTo(4));
            pregnancyBaseline = pregnancy.OriginalBelly!.Size;
            var prefs = entities.GetComponent<InteractionPanelPreferencesComponent>(recipient);
            prefs.Profile = prefs.Profile.WithArousalPreferences(prefs.Profile.ArousalPreferences with { Pregnancy = false });
        });
        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<BlueMoonPhysiologyComponent>(recipient).Pregnant, Is.False);
            Assert.That(entities.GetComponent<BlueMoonPhysiologyComponent>(recipient).PregnancyStage, Is.Zero);
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(recipient).BodyFeatures.Single(f => f.Part == "belly").Size, Is.EqualTo(pregnancyBaseline));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            entities.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }
}
