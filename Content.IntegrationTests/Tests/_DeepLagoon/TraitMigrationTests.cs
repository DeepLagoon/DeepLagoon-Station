using System.Numerics;
using System.Reflection;
using Content.Shared.Weapons.Reflect;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.StatusEffect;
using Content.Shared.Forensics;
using Content.Shared.Body.Systems;
using Content.Shared.Body.Part;
using Content.Shared.Prying.Components;
using Content.Shared.PowerCell.Components;
using Content.Shared.Weapons.Melee;
using System.Linq;
using Content.Server.Traits;
using Content.Server.Instruments;
using Content.Shared.Abilities.Psionics;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Content.Shared.Traits;
using Content.Shared.Traits.Assorted.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class TraitMigrationTests
{
    [Test]
    public async Task SharedBudgetPreservesProfilesAndRejectsInvalidCombinations()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var config = pair.Server.ResolveDependency<IConfigurationManager>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            Assert.That(TraitSelection.Evaluate(profile, prototypes, config).Points, Is.EqualTo(12));
            var profileA = profile.WithTraitPreference("Vigor", prototypes).WithTraitPreference("BionicLegs", prototypes)
                .WithTraitPreference("Hemophilia", prototypes);
            var profileB = profile.WithTraitPreference("Hemophilia", prototypes).WithTraitPreference("BionicLegs", prototypes)
                .WithTraitPreference("Vigor", prototypes);
            Assert.That(TraitSelection.Evaluate(profileA, prototypes, config).Valid, Is.True);
            Assert.That(TraitSelection.Evaluate(profileA, prototypes, config).Points,
                Is.EqualTo(TraitSelection.Evaluate(profileB, prototypes, config).Points));
            Assert.That(profileA.GetValidTraits(profileA.TraitPreferences, prototypes), Is.EquivalentTo(profileB.TraitPreferences));
            var conflict = profile.WithTraitPreference("Vigor", prototypes).WithTraitPreference("Lethargy", prototypes);
            Assert.That(TraitSelection.Evaluate(conflict, prototypes, config).Valid, Is.False);
            Assert.That(TraitSelection.Evaluate(HumanoidCharacterProfile.DefaultWithSpecies("Oni")
                .WithTraitPreference("Vigor", prototypes), prototypes, config).Valid, Is.False);
            var overspent = profile.WithTraitPreference("LatentPsychic", prototypes).WithTraitPreference("HighPotential", prototypes);
            Assert.That(TraitSelection.Evaluate(overspent, prototypes, config).Points, Is.LessThan(0));
            Assert.That(TraitSelection.Evaluate(overspent, prototypes, config).Valid, Is.False);
            Assert.That(overspent.GetValidTraits(overspent.TraitPreferences, prototypes).Count, Is.EqualTo(2),
                "Saved selections must remain repairable rather than silently losing traits after a balance change");
            Assert.That(TraitSelection.Evaluate(profile.WithTraitPreference("GeneticModification", prototypes), prototypes, config).Slots, Is.Zero);
            var allTraits = profile;
            foreach (var trait in prototypes.EnumeratePrototypes<TraitPrototype>().Where(t => !t.Disabled))
                allTraits = allTraits.WithTraitPreference(trait.ID, prototypes);
            Assert.That(TraitSelection.Evaluate(allTraits, prototypes, config).Slots, Is.GreaterThan(14));
            Assert.That(TraitSelection.Evaluate(allTraits, prototypes, config).Valid, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ImportedEffectsApplyToNativeBodies()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var serialization = pair.Server.ResolveDependency<ISerializationManager>();
            var factory = pair.Server.ResolveDependency<IComponentFactory>();
            var locale = pair.Server.ResolveDependency<ILocalizationManager>();
            var imported = prototypes.EnumeratePrototypes<TraitPrototype>().Where(t => t.Name.ToString() == "trait-name-" + t.ID).ToArray();
            Assert.That(imported.Length, Is.EqualTo(67));
            Assert.That(prototypes.EnumeratePrototypes<TraitPrototype>().Count(), Is.EqualTo(137));
            Assert.Multiple(() =>
            {
                foreach (var trait in imported)
                foreach (var patch in trait.Functions.OfType<TraitModifyComponent>().SelectMany(f => f.Components))
                foreach (var (name, _) in patch.Components)
                foreach (var field in patch.Fields)
                    Assert.That(factory.GetRegistration(name).Type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Any(m => m.GetCustomAttribute<Robust.Shared.Serialization.Manager.Attributes.DataFieldAttribute>() is { } attr
                            && (attr.Tag ?? char.ToLowerInvariant(m.Name[0]) + m.Name[1..]) == field), Is.True, $"{trait.ID}: {name}.{field}");
            });
            var oni = entities.SpawnEntity("MobOni", map.GridCoords);
            var knife = entities.SpawnEntity("CombatKnife", map.GridCoords);
            var melee = entities.System<SharedMeleeWeaponSystem>();
            var baseline = melee.GetDamage(knife, oni);
            foreach (var function in prototypes.Index<TraitPrototype>("Swashbuckler").Functions)
                function.OnPlayerSpawn(oni, factory, entities, serialization);
            var specialized = melee.GetDamage(knife, oni);
            foreach (var (type, damage) in baseline.DamageDict)
            {
                var factor = type == "Slash" ? 1.35f : type is "Blunt" or "Piercing" ? 1.25f : 1.2f;
                Assert.That((float) specialized.DamageDict[type], Is.EqualTo((float) damage / 1.2f * factor).Within(0.03f));
            }
            entities.DeleteEntity(knife);
            entities.DeleteEntity(oni);
            foreach (var trait in imported)
            {
                Assert.That(locale.HasString(trait.Name), Is.True, trait.ID);
                Assert.That(trait.Description is { } desc && locale.HasString(desc), Is.True, trait.ID);
                var human = entities.SpawnEntity("MobHuman", map.GridCoords);
                try
                {
                    Assert.DoesNotThrow(() =>
                    {
                        foreach (var function in trait.Functions)
                            function.OnPlayerSpawn(human, factory, entities, serialization);
                    }, trait.ID);
                    if (trait.ID == "MartialArtist")
                        Assert.That(entities.GetComponent<MeleeWeaponComponent>(human).HeavyOnLightMiss, Is.True);
                    if (trait.ID == "Shell")
                        Assert.That(entities.GetComponent<Content.Shared.Speech.Components.VocalComponent>(human).EmoteSounds?.ID, Is.EqualTo("UnisexIPC"));
                    if (trait.ID == "BionicArm")
                        Assert.That(entities.GetComponent<PryingComponent>(human).PryPowered, Is.True);
                    if (trait.ID == "BionicPryArm")
                        Assert.That(entities.GetComponent<PryingComponent>(human).PryPowered, Is.False);
                    if (trait.ID == "Shell")
                    {
                        Assert.That(entities.GetComponent<PowerCellSlotComponent>(human).CellSlotId, Is.EqualTo("cell_slot"));
                        Assert.That(entities.GetComponent<Content.Shared.Containers.ItemSlots.ItemSlotsComponent>(human).Slots["cell_slot"].Item,
                            Is.Not.Null, "Shell must start with an actual power cell");
                    }
                    if (trait.ID == "LightAmplification")
                        Assert.That(entities.GetComponent<Content.Shared.Overlays.NightVisionComponent>(human).Enabled, Is.True);
                    if (trait.ID is "CyberneticLeftLeg" or "CyberneticRightLeg")
                        Assert.That(entities.System<SharedBodySystem>().GetBodyChildrenOfType(human, BodyPartType.Leg)
                            .Any(part => entities.GetComponent<MetaDataComponent>(part.Id).EntityPrototype?.ID ==
                                (trait.ID == "CyberneticLeftLeg" ? "LeftLegCybernetic" : "RightLegCybernetic")), Is.True, trait.ID);
                    if (trait.ID == "Photophobia")
                    {
                        entities.GetComponent<EyeDamageOnFlashingComponent>(human).EyeDamageChance = 1f;
                        entities.System<Content.Server.Flash.FlashSystem>().Flash(human, null, null, TimeSpan.FromSeconds(1), 0.8f, displayPopup: false);
                        Assert.That(entities.GetComponent<BlindableComponent>(human).EyeDamage, Is.EqualTo(1));
                        Assert.That(entities.System<StatusEffectsSystem>().TryGetTime(human, "Flashed", out var flashTime), Is.True);
                        Assert.That((flashTime!.Value.Item2 - flashTime.Value.Item1).TotalSeconds, Is.EqualTo(1.5).Within(0.01));
                    }
                    if (trait.ID == "G2Chassis")
                    {
                        var reflect = entities.GetComponent<ReflectComponent>(human);
                        Assert.That(reflect.Innate, Is.True);
                        reflect.ReflectProb = 1f;
                        var reflected = new HitScanReflectAttemptEvent(null, human, ReflectType.Energy, Vector2.UnitX, false, null);
                        entities.EventBus.RaiseLocalEvent(human, ref reflected);
                        Assert.That(reflected.Reflected, Is.True, "Innate reflection must work without an item toggle");
                    }
                    if (trait.ID == "PowerOverwhelming")
                        Assert.That(entities.GetComponent<PsionicComponent>(human).ActivePowers.Any(p => p.ID == "PowerOverwhelming"), Is.True);
                    if (trait.ID == "Singer" || trait.ID == "LyreBird")
                        Assert.That(entities.GetComponent<InstrumentComponent>(human).AllowProgramChange, Is.EqualTo(trait.ID == "LyreBird"));
                    if (trait.ID == "PsychoHistorian")
                    {
                        Assert.That(entities.GetComponent<PsionicComponent>(human).BaselinePowerCost, Is.EqualTo(75));
                        Assert.That(entities.GetComponent<PsionicComponent>(human).ActivePowers.Any(p => p.ID == "TelepathyPower"), Is.True);
                    }
                }
                finally { entities.DeleteEntity(human); }
            }
        });
        EntityUid vulp = default;
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            vulp = entities.SpawnEntity("MobVulpkanin", map.GridCoords);
            Assert.That(entities.HasComponent<ScentTrackerComponent>(vulp), Is.True);
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            foreach (var function in prototypes.Index<TraitPrototype>("StuntedSnout").Functions)
                function.OnPlayerSpawn(vulp, pair.Server.ResolveDependency<IComponentFactory>(), entities,
                    pair.Server.ResolveDependency<ISerializationManager>());
        });
        await pair.Server.WaitRunTicks(1);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.HasComponent<ScentTrackerComponent>(vulp), Is.False);
            pair.Server.EntMan.DeleteEntity(vulp);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ServerRejectsWholeIllegalSelectionAndAppliesValidOne()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var map = await pair.CreateTestMap();
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var job = prototypes.EnumeratePrototypes<Content.Shared.Roles.JobPrototype>().First(j => j.ApplyTraits).ID;
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            var system = entities.System<TraitSystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithTraitPreference("HighAdrenaline", prototypes).WithTraitPreference("AdrenalDysfunction", prototypes);
            system.ApplyTraits(human, job, profile);
            Assert.That(entities.HasComponent<AdrenalineComponent>(human), Is.False);
            system.ApplyTraits(human, job, HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithTraitPreference("HighAdrenaline", prototypes));
            Assert.That(entities.HasComponent<AdrenalineComponent>(human), Is.True);
            var noTraitsJob = prototypes.EnumeratePrototypes<Content.Shared.Roles.JobPrototype>().First(j => !j.ApplyTraits).ID;
            var other = entities.SpawnEntity("MobHuman", map.GridCoords);
            system.ApplyTraits(other, noTraitsJob, HumanoidCharacterProfile.DefaultWithSpecies("Human")
                .WithTraitPreference("HighAdrenaline", prototypes));
            Assert.That(entities.HasComponent<AdrenalineComponent>(other), Is.False);
            var psiJob = prototypes.EnumeratePrototypes<Content.Shared.Roles.JobPrototype>()
                .First(j => j.ApplyTraits && j.ID is not ("Borg" or "MedicalBorg" or "ResearchDirector" or "ForensicMantis"
                    or "Chaplain" or "Librarian" or "Prisoner")).ID;
            var ipc = entities.SpawnEntity("MobIPC", map.GridCoords);
            var psiProfile = HumanoidCharacterProfile.DefaultWithSpecies("IPC");
            foreach (var id in new[] { "PowerOverwhelming", "PsychoHistorian", "AnomalousPositronics", "IPCBrittleBoneDisease", "IPCFragileCircuits" })
                psiProfile = psiProfile.WithTraitPreference(id, prototypes);
            Assert.That(TraitSelection.Evaluate(psiProfile, prototypes, pair.Server.CfgMan, psiJob).Valid, Is.True);
            system.ApplyTraits(ipc, psiJob, psiProfile);
            var psi = entities.GetComponent<PsionicComponent>(ipc);
            Assert.That(psi.BaselinePowerCost, Is.EqualTo(75));
            Assert.That(psi.ActivePowers.Select(p => p.ID), Does.Contain("PowerOverwhelming").And.Contain("TelepathyPower"));
            entities.DeleteEntity(ipc);
            entities.DeleteEntity(human);
            entities.DeleteEntity(other);
        });
        await pair.CleanReturnAsync();
    }
}
