using System.Linq;
using Content.Shared.Body.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Robust.Client.ResourceManagement;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using ClientAppearance = Content.Client.Humanoid.HumanoidAppearanceSystem;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Robust.Client.UserInterface;
using Robust.Client.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class SynthSpeciesTests
{
    [Test]
    public async Task EditorReplacesShellsAndKeepsHandsAndFeetWithTheirLimbs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            var saved = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
            editor.SetProfile(saved, editor.CharacterSlot);
            bool Choose(string id) => editor.HandleTguiProfileAction("marking-add", JsonSerializer.Serialize(new { value = id }));
            string[] Selected() => editor.Profile!.Appearance.Markings.Select(m => m.MarkingId).ToArray();
            Assert.That(Choose("SynthHumanChest"), Is.True);
            Assert.That(Choose("SynthMechanicalChest"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthMechanicalChest").And.Not.Contain("SynthHumanChest"));
            var torsoSprite = pair.Client.EntMan.GetComponent<SpriteComponent>(editor.PreviewDummy);
            Assert.That(torsoSprite.LayerMapTryGet("SynthHumanChest-torso_m", out _), Is.False);
            Assert.That(torsoSprite.LayerMapTryGet("SynthMechanicalChest-torso_m", out _), Is.True);
            Assert.That(((SpriteComponent.Layer) torsoSprite[torsoSprite.LayerMapGet(HumanoidVisualLayers.Chest)]).ActualState,
                Is.Null, "A shell marking must not retain the dummy's original human torso underneath");
            Assert.That(Choose("SynthMechanicalLArm"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthMechanicalLHand"));
            Assert.That(Selected(), Does.Contain("SynthHumanRArm").And.Contain("SynthHumanRHand"));
            Assert.That(Choose("SynthVulpkaninLArm"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthVulpkaninLArm").And.Contain("SynthVulpkaninLHand"));
            Assert.That(Selected(), Does.Not.Contain("SynthMechanicalLArm").And.Not.Contain("SynthMechanicalLHand"));
            Assert.That(Choose("SynthFeroxiRArm"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthVulpkaninLArm").And.Contain("SynthFeroxiRHand"));
            var armIndex = editor.Profile!.Appearance.Markings.FindIndex(m => m.MarkingId == "SynthVulpkaninLArm");
            Assert.That(editor.HandleTguiProfileAction("marking-color", JsonSerializer.Serialize(new
                { index = armIndex, layer = 0, value = "#123456" })), Is.True);
            Assert.That(editor.Profile.Appearance.Markings.Single(m => m.MarkingId == "SynthVulpkaninLHand").MarkingColors[0],
                Is.EqualTo(Color.FromHex("#123456")));
            Assert.That(Choose("SynthMechanicalLLegDigitigrade"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthMechanicalLFootDigitigrade"));
            Assert.That(Choose("SynthHumanLLeg"), Is.True);
            Assert.That(Selected(), Does.Contain("SynthHumanLFoot").And.Not.Contain("SynthMechanicalLFootDigitigrade"));
            Assert.That(Choose("SynthMechanicalLHand"), Is.False);
            Assert.That(Choose("SynthMechanicalHead"), Is.True);
            Assert.That(Choose("SynthVulpkaninHead"), Is.True);
            Assert.That(Selected(), Does.Not.Contain("SynthMechanicalHead"));
            Assert.That(Choose("SynthVulpkaninHead"), Is.True);
            Assert.That(Selected().Count(id => id == "SynthVulpkaninHead"), Is.EqualTo(1));
            using var data = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var options = data.RootElement.GetProperty("markingOptions").EnumerateArray()
                .Select(option => option.GetProperty("id").GetString()).ToArray();
            Assert.That(options, Does.Contain("SynthMechanicalLArm").And.Not.Contain("SynthMechanicalLHand"));
            Assert.That(options, Does.Not.Contain("SynthMechanicalLFootDigitigrade"));
            armIndex = editor.Profile!.Appearance.Markings.FindIndex(m => m.MarkingId == "SynthVulpkaninLArm");
            Assert.That(editor.HandleTguiProfileAction("marking-remove", JsonSerializer.Serialize(new { index = armIndex })), Is.True);
            Assert.That(Selected(), Does.Contain("SynthHumanLArm").And.Contain("SynthHumanLHand"));
            Assert.That(Selected(), Does.Contain("SynthFeroxiRArm").And.Contain("SynthFeroxiRHand"));
            Assert.That(saved.Appearance.Markings, Is.Empty, "Edits must only change the draft");
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EditorDummyLoadsShellsForBothSexesAndReloadsSkinColor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var appearance = entities.System<ClientAppearance>();
            var species = prototypes.Index<SpeciesPrototype>("Synth");
            var dummy = entities.SpawnEntity(species.DollPrototype, MapCoordinates.Nullspace);
            foreach (var sex in new[] { Sex.Male, Sex.Female })
            foreach (var shell in new[] { "Human", "Vulpkanin", "Feroxi", "Mechanical" })
            {
                appearance.LoadProfile(dummy, HumanoidCharacterProfile.DefaultWithSpecies("Human"));
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth").WithSex(sex);
                var head = prototypes.Index<MarkingPrototype>($"Synth{shell}Head").AsMarking();
                profile = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new() { head }));
                appearance.LoadProfile(dummy, profile);
                profile = profile.WithCharacterAppearance(profile.Appearance.WithSkinColor(Color.Blue));
                appearance.LoadProfile(dummy, profile);
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
                Assert.That(humanoid.BaseLayers.ContainsKey(HumanoidVisualLayers.Head), Is.True);
                Assert.That(humanoid.BaseLayers.ContainsKey(HumanoidVisualLayers.Chest), Is.True);
                Assert.That(humanoid.SkinColor, Is.EqualTo(Color.Blue));
                var bodySprite = entities.GetComponent<SpriteComponent>(dummy);
                Assert.That(((SpriteComponent.Layer) bodySprite[bodySprite.LayerMapGet(HumanoidVisualLayers.Chest)]).ActualState,
                    Is.Null, "Switching from a human profile must clear its base torso before applying the synthetic shell");
            }
            var tail = prototypes.Index<MarkingPrototype>("SynthAccessorySynthTail").AsMarking();
            tail.SetColor(Color.Magenta);
            var tailProfile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
            tailProfile.Appearance.Markings.Add(tail);
            appearance.LoadProfile(dummy, tailProfile);
            var sprite = entities.GetComponent<SpriteComponent>(dummy);
            Assert.That(sprite.LayerMapGet("SynthAccessorySynthTail-synth_tail_forward"),
                Is.LessThan(sprite.LayerMapGet(HumanoidVisualLayers.Chest)));
            Assert.That(sprite.LayerMapGet("SynthAccessorySynthTail-synth_tail_forward"),
                Is.LessThan(sprite.LayerMapGet(HumanoidVisualLayers.LLeg)));
            Assert.That(sprite.LayerMapGet("SynthAccessorySynthTail-synth_tail_back"),
                Is.GreaterThan(sprite.LayerMapGet(HumanoidVisualLayers.Chest)));
            entities.DeleteEntity(dummy);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SelectableShellsHaveResourcesAndSpawnWithSyntheticAnatomy()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await pair.Client.WaitAssertion(() =>
        {
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var resources = pair.Client.ResolveDependency<IResourceCache>();
            var markings = prototypes.EnumeratePrototypes<MarkingPrototype>()
                .Where(p => p.SpeciesRestrictions?.Contains("Synth") == true).ToArray();
            Assert.That(markings, Is.Not.Empty);
            foreach (var marking in markings)
            foreach (var sprite in marking.Sprites.Cast<SpriteSpecifier.Rsi>())
            {
                var resource = resources.GetResource<RSIResource>(new ResPath("/Textures") / sprite.RsiPath);
                Assert.That(resource.RSI.TryGetState(sprite.RsiState, out _), Is.True, marking.ID);
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var species = prototypes.Index<SpeciesPrototype>("Synth");
            Assert.That(species.RoundStart, Is.True);
            foreach (var prototype in new[] { species.Prototype, species.DollPrototype })
            {
                var mob = entities.SpawnEntity(prototype, map.GridCoords);
                var appearance = entities.System<SharedHumanoidAppearanceSystem>();
                appearance.LoadProfile(mob, HumanoidCharacterProfile.DefaultWithSpecies("Synth"));
                var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(mob);
                Assert.That(humanoid.Species.Id, Is.EqualTo("Synth"));
                Assert.That(humanoid.MarkingSet.Markings.Values.SelectMany(m => m).Count(), Is.GreaterThanOrEqualTo(10));
                if (prototype == species.Prototype)
                    Assert.That(entities.GetComponent<BodyComponent>(mob).Prototype, Is.EqualTo("Synth"));
                entities.DeleteEntity(mob);
            }
            foreach (var shell in new[] { "Human", "Vulpkanin", "Feroxi", "Mechanical" })
            {
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
                var head = prototypes.Index<MarkingPrototype>($"Synth{shell}Head").AsMarking();
                var valid = HumanoidCharacterAppearance.EnsureValid(
                    profile.Appearance.WithMarkings(new() { head }), "Synth", profile.Sex);
                Assert.That(valid.Markings.Any(m => m.MarkingId == head.MarkingId), Is.True, shell);
            }
        });
        await pair.CleanReturnAsync();
    }
}
