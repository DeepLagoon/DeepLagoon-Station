using System.Linq;
using System.Text.Json;
using Content.Client._Mono.Company;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client.Players.PlayTimeTracking;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Content.Shared.Traits;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class SpeciesAppearanceTests
{
    [Test]
    public async Task CatalogueTypesSeparateEarsFromOtherHeadFeatures()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            using var editor = Editor(pair.Client);
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Synth"), 0);
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var options = state.RootElement.GetProperty("markingOptions").EnumerateArray()
                .ToDictionary(option => option.GetProperty("id").GetString()!, option => option.GetProperty("typeId").GetString());
            foreach (var id in new[] { "BlueMoonEarsHumanBearHeadSide", "VulpEarFennec", "SynthVulpEarFennec",
                         "CatEars", "HarpyEarsDefault", "LizardHornsKoboldEars" })
                Assert.That(options[id], Is.EqualTo("Ears"), id);
            foreach (var id in new[] { "LizardFrillsAquatic", "RobotAntennaTv", "ScreenBlue", "OniHornBull", "RodentiaCheeksRound" })
                Assert.That(options[id], Is.Empty, id);
            Assert.That(options["BlueMoonTailsHumanFoxTail"], Is.EqualTo("Tail"));
            Assert.That(options["VoxTailStripes"], Is.EqualTo("Tail"));
            Assert.That(options["TajaranHeadMuzzle"], Is.EqualTo("Snout"));
            Assert.That(options["BlueMoonDeco_wingsWyvernFoldedChest"], Is.EqualTo("Wings"));
            Assert.That(options["HarpyWingDefaultHuescale"], Is.EqualTo("Wings"));
            foreach (var (id, type) in options)
            {
                var prototype = prototypes.Index<MarkingPrototype>(id);
                if (prototype.MarkingCategory is MarkingCategories.Tail or MarkingCategories.TailExtras)
                    Assert.That(type, Is.EqualTo("Tail"), id);
                if (prototype.MarkingCategory == MarkingCategories.Snout)
                    Assert.That(type, Is.EqualTo("Snout"), id);
                if (prototype.MarkingCategory == MarkingCategories.Wings)
                    Assert.That(type, Is.EqualTo("Wings"), id);
            }
            Assert.That(options.Values, Does.Contain("Snout"));
        });
        await pair.CleanReturnAsync();
    }

    private static HumanoidProfileEditor Editor(Robust.UnitTesting.RobustIntegrationTest.ClientIntegrationInstance client) => new(
        client.ResolveDependency<IClientPreferencesManager>(), client.ResolveDependency<IConfigurationManager>(),
        client.ResolveDependency<IEntityManager>(), client.ResolveDependency<IFileDialogManager>(),
        client.ResolveDependency<ILogManager>(), client.ResolveDependency<IPlayerManager>(),
        client.ResolveDependency<IPrototypeManager>(), client.ResolveDependency<IResourceCache>(),
        client.ResolveDependency<JobRequirementsManager>(), client.ResolveDependency<MarkingManager>(),
        client.ResolveDependency<CompanyManager>());

    [Test]
    public async Task SpeciesCataloguesTraitToggleAndExclusiveChoices()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var manager = pair.Client.ResolveDependency<MarkingManager>();
            var bear = prototypes.Index<MarkingPrototype>("BlueMoonEarsHumanBearHeadSide");
            var fox = prototypes.Index<MarkingPrototype>("BlueMoonTailsHumanFoxTail");
            var cat = prototypes.Index<MarkingPrototype>("BlueMoonTailsHumanCatTail");
            Assert.That(manager.IsSpeciesAllowed("Human", bear), Is.False);
            Assert.That(manager.IsSpeciesAllowed("Vulpkanin", bear), Is.False);
            Assert.That(manager.IsSpeciesAllowed("Vulpkanin", fox), Is.True);
            Assert.That(manager.IsSpeciesAllowed("Tajaran", cat), Is.True);
            Assert.That(manager.IsSpeciesAllowed("Human", fox), Is.False);
            Assert.That(manager.IsSpeciesAllowed("Synth", bear), Is.True);
            Assert.That(manager.GetSpeciesCategories(bear), Is.Empty);
            Assert.That(manager.GetSpeciesCategories(fox), Is.EquivalentTo(new[] { "Vulpkanin" }));
            Assert.That(manager.GetSpeciesCategories(cat), Is.EquivalentTo(new[] { "Tajaran", "Ariral" }));
            Assert.That(prototypes.Index<TraitPrototype>("GeneticModification").Cost, Is.Zero);

            using var editor = Editor(pair.Client);
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), 0);
            bool HasBear()
            {
                using var data = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
                Assert.That(data.RootElement.GetProperty("markingSpeciesRestricted").GetBoolean(),
                    Is.EqualTo(!editor.Profile!.UnrestrictedMarkings && editor.Profile.Species.Id != "Synth"));
                Assert.That(data.RootElement.GetProperty("speciesName").GetString(),
                    Is.EqualTo(Robust.Shared.Localization.Loc.GetString(prototypes.Index(editor.Profile.Species).Name)));
                return data.RootElement.GetProperty("markingOptions").EnumerateArray().Any(p => p.GetProperty("id").GetString() == bear.ID);
            }
            Assert.That(HasBear(), Is.False);
            Assert.That(editor.HandleTguiProfileAction("marking-add", JsonSerializer.Serialize(new { value = bear.ID })), Is.False);
            Assert.That(editor.HandleTguiProfileAction("trait", "{\"id\":\"GeneticModification\"}"), Is.True);
            Assert.That(HasBear(), Is.True);
            using (var catalogue = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString()))
            {
                var options = catalogue.RootElement.GetProperty("markingOptions").EnumerateArray().ToArray();
                Assert.That(options.Single(option => option.GetProperty("id").GetString() == bear.ID)
                    .GetProperty("races")[0].GetProperty("id").GetString(), Is.EqualTo("genetic"));
                Assert.That(options.Single(option => option.GetProperty("id").GetString() == cat.ID)
                    .GetProperty("races").EnumerateArray().Select(race => race.GetProperty("id").GetString()),
                    Is.EquivalentTo(new[] { "Tajaran", "Ariral" }));
                Assert.That(options.Any(option => option.GetProperty("races").EnumerateArray()
                    .Any(race => race.GetProperty("id").GetString() == "common")), Is.True);
            }
            Assert.That(editor.HandleTguiProfileAction("marking-add", JsonSerializer.Serialize(new { value = bear.ID })), Is.True);
            var valid = HumanoidCharacterAppearance.EnsureValid(editor.Profile!.Appearance, "Human", editor.Profile.Sex, true);
            Assert.That(valid.Markings.Any(p => p.MarkingId == bear.ID), Is.True);
            Assert.That(editor.HandleTguiProfileAction("trait", "{\"id\":\"GeneticModification\"}"), Is.True);
            Assert.That(HasBear(), Is.False);
            Assert.That(editor.Profile!.Appearance.Markings.Any(p => p.MarkingId == bear.ID), Is.False);

            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Synth"), 0);
            Assert.That(HasBear(), Is.True);
            foreach (var category in new[] { MarkingCategories.Tail, MarkingCategories.Snout })
            {
                var choices = manager.Markings.Values.Where(p => p.ID.StartsWith("BlueMoon") && p.MarkingCategory == category
                    && p.SexRestriction == null && !p.EditorHidden).Take(2).ToArray();
                foreach (var marking in choices)
                    Assert.That(editor.HandleTguiProfileAction("marking-add", JsonSerializer.Serialize(new { value = marking.ID })), Is.True);
                Assert.That(editor.Profile!.Appearance.Markings.Count(p => manager.Markings[p.MarkingId].BodyPart == choices[0].BodyPart), Is.EqualTo(1), category.ToString());
                Assert.That(editor.HandleTguiProfileAction("marking-combine", JsonSerializer.Serialize(new { value = manager.IsCommon(choices[0]) ? "common" : choices[0].MarkingCategory.ToString() })), Is.True);
                Assert.That(editor.HandleTguiProfileAction("marking-add", JsonSerializer.Serialize(new { value = choices[0].ID })), Is.True);
                Assert.That(editor.Profile!.Appearance.Markings.Count(p => manager.Markings[p.MarkingId].BodyPart == choices[0].BodyPart), Is.EqualTo(2), category.ToString());
                Assert.That(editor.HandleTguiProfileAction("marking-combine", JsonSerializer.Serialize(new { value = manager.IsCommon(choices[0]) ? "common" : choices[0].MarkingCategory.ToString() })), Is.True);
                Assert.That(editor.Profile!.Appearance.Markings.Count(p => manager.Markings[p.MarkingId].BodyPart == choices[0].BodyPart), Is.EqualTo(1), category.ToString());
            }
            using var commonData = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            foreach (var marking in commonData.RootElement.GetProperty("markings").EnumerateArray())
                Assert.That(marking.GetProperty("category").GetString(), Is.EqualTo(Robust.Shared.Localization.Loc.GetString(
                    "markings-category-" + marking.GetProperty("categoryId").GetString())));
            Assert.That(commonData.RootElement.GetProperty("markingOptions").EnumerateArray().Any(p => p.GetProperty("category").GetString() is "Общие" or "Common"), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AppearanceUndoRestoresOnlyThisTabAndRedoIsInvalidatedByNewEdits()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
            profile.Appearance.Markings.Clear();
            profile.Appearance.Tattoos.Add(new Content.Shared._DeepLagoon.Tattoos.Tattoo());
            using var editor = Editor(pair.Client);
            editor.SetProfile(profile, 0);
            bool Action(string action, object? args = null) => editor.HandleTguiProfileAction(action, JsonSerializer.Serialize(args ?? new { }));
            bool Flag(string name)
            {
                using var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings", false).ToString());
                return name == "combineMarkings" ? state.RootElement.GetProperty("combinedMarkingCategories").EnumerateArray().Any(category => category.GetProperty("id").GetString() == "Tail") : state.RootElement.GetProperty(name).GetBoolean();
            }
            Assert.That(Flag("canUndoAppearance"), Is.False);
            Assert.That(Action("marking-add", new { value = "BlueMoonEarsHumanBearHeadSide" }), Is.True);
            Assert.That(Action("tattoo-clear"), Is.True);
            Assert.That(Action("name", new { value = "Other Name" }), Is.True);
            Assert.That(Action("hair-color", new { value = "#123456" }), Is.True);
            Assert.That(Flag("canUndoAppearance"), Is.True);
            var changed = editor.Profile!.Appearance.Markings.Select(m => new Marking(m)).ToList();
            var hair = editor.Profile.Appearance.HairColor;
            Assert.That(Action("appearance-undo"), Is.True);
            Assert.That(editor.Profile.Appearance.Markings, Is.Empty);
            Assert.That(editor.Profile.Appearance.Tattoos.Single().Id, Is.EqualTo(profile.Appearance.Tattoos.Single().Id));
            Assert.That(editor.Profile.Name, Is.EqualTo("Other Name"));
            Assert.That(editor.Profile.Appearance.HairColor, Is.EqualTo(hair));
            Assert.That(Flag("canUndoAppearance"), Is.False);
            Assert.That(Flag("canRedoAppearance"), Is.True);
            Assert.That(Action("appearance-redo"), Is.True);
            Assert.That(editor.Profile.Appearance.Markings, Is.EqualTo(changed));
            Assert.That(editor.Profile.Appearance.Tattoos, Is.Empty);
            Assert.That(Action("appearance-undo"), Is.True);
            Assert.That(Action("marking-add", new { value = "BlueMoonTailsHumanFoxTail" }), Is.True);
            Assert.That(Flag("canRedoAppearance"), Is.False);
            Assert.That(Action("appearance-redo"), Is.False);
            Assert.That(Action("marking-combine", new { value = "Tail" }), Is.True);
            Assert.That(Action("marking-add", new { value = "BlueMoonTailsHumanCatTail" }), Is.True);
            Assert.That(editor.Profile.Appearance.Markings.Count(m => prototypes.Index<MarkingPrototype>(m.MarkingId).BodyPart == HumanoidVisualLayers.Tail), Is.EqualTo(2));
            Assert.That(Action("appearance-undo"), Is.True);
            Assert.That(Flag("combineMarkings"), Is.False);
            Assert.That(Action("appearance-redo"), Is.True);
            Assert.That(Flag("combineMarkings"), Is.True);
            Assert.That(editor.Profile.Appearance.Markings.Count(m => prototypes.Index<MarkingPrototype>(m.MarkingId).BodyPart == HumanoidVisualLayers.Tail), Is.EqualTo(2));

            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), 1);
            Assert.That(Flag("canUndoAppearance"), Is.False);
            Assert.That(Flag("canRedoAppearance"), Is.False);
            Assert.That(Action("trait", new { id = "GeneticModification" }), Is.True);
            Assert.That(Action("marking-add", new { value = "BlueMoonEarsHumanBearHeadSide" }), Is.True);
            Assert.That(Action("appearance-undo"), Is.True);
            Assert.That(Action("trait", new { id = "GeneticModification" }), Is.True);
            Action("appearance-redo");
            Assert.That(editor.Profile!.Appearance.Markings.Any(m => m.MarkingId == "BlueMoonEarsHumanBearHeadSide"), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ServerAndClientKeepForeignAnatomyOnlyWhenAuthorized()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        const string id = "BlueMoonEarsHumanBearHeadSide";
        foreach (var modified in new[] { false, true })
        foreach (var species in new[] { "Human", "Synth" })
        {
            await pair.Server.WaitAssertion(() =>
            {
                var entities = pair.Server.EntMan;
                var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
                var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                if (modified) profile = profile.WithTraitPreference("GeneticModification", prototypes);
                profile.Appearance.Markings.Add(prototypes.Index<MarkingPrototype>(id).AsMarking());
                var dummy = entities.SpawnEntity("Mob" + species + "Dummy", MapCoordinates.Nullspace);
                try
                {
                    entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(dummy, profile);
                    var appearance = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
                    Assert.That(appearance.MarkingSet.Markings.Values.SelectMany(list => list).Any(p => p.MarkingId == id), Is.EqualTo(modified || species == "Synth"));
                }
                finally { entities.DeleteEntity(dummy); }
            });
            await pair.Client.WaitAssertion(() =>
            {
                var entities = pair.Client.EntMan;
                var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
                var profile = HumanoidCharacterProfile.DefaultWithSpecies(species);
                if (modified) profile = profile.WithTraitPreference("GeneticModification", prototypes);
                var marking = prototypes.Index<MarkingPrototype>(id);
                profile.Appearance.Markings.Add(marking.AsMarking());
                var dummy = entities.SpawnEntity("Mob" + species + "Dummy", MapCoordinates.Nullspace);
                try
                {
                    entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>().LoadProfile(dummy, profile);
                    var sprite = entities.GetComponent<SpriteComponent>(dummy);
                    var key = id + "-" + ((SpriteSpecifier.Rsi)marking.Sprites[0]).RsiState;
                    Assert.That(sprite.LayerMapTryGet(key, out var index) && sprite[index].Visible, Is.EqualTo(modified || species == "Synth"));
                }
                finally { entities.DeleteEntity(dummy); }
            });
        }
        await pair.CleanReturnAsync();
    }
}
