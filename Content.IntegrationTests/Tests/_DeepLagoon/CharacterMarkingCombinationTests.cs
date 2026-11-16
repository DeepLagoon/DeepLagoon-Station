using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterMarkingCombinationTests
{
    [TestCase("Human", "HeadTop", "HumanLongEars", "LongEarsWide", "LongEarsSmall")]
    [TestCase("Moth", "Antennae", "MothAntennasRoyal", "MothAntennasMaple", "MothAntennasFeathery")]
    [TestCase("Synth", "HeadTop", "HumanLongEars", "BlueMoonEarsCatHeadSide", "LongEarsSmall")]
    public async Task LayeredHeadMarkingsSurviveValidationAndRender(string species, string category,
        string first, string second, string third)
    {
        var choices = new[] { first, second, third };
        HumanoidCharacterProfile saved = default!;
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor)typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies(species), editor.CharacterSlot);

            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var tab = state.RootElement.GetProperty("tabs").EnumerateArray()
                .Single(entry => entry.GetProperty("mode").GetString() == "markings").GetProperty("id").GetInt32();
            Assert.That(editor.HandleTguiProfileAction("select-tab", JsonSerializer.Serialize(new { value = tab })), Is.True);
            var options = state.RootElement.GetProperty("markingOptions").EnumerateArray()
                .Where(option => choices.Contains(option.GetProperty("id").GetString())).ToArray();
            Assert.That(options.Select(option => option.GetProperty("id").GetString()), Is.EquivalentTo(choices));
            Assert.That(options.Select(option => option.GetProperty("combinationCategory").GetString()), Is.All.EqualTo(category));

            bool Action(string action, string value) =>
                editor.HandleTguiProfileAction(action, JsonSerializer.Serialize(new { value }));

            string[] Selected() => editor.Profile!.Appearance.Markings.Select(marking => marking.MarkingId)
                .Where(choices.Contains).ToArray();

            Assert.That(Action("marking-toggle", first), Is.True);
            Assert.That(Action("marking-toggle", second), Is.True);
            Assert.That(Selected(), Is.EqualTo(new[] { second }));

            Assert.That(Action("marking-combine", category), Is.True);
            Assert.That(Action("marking-toggle", first), Is.True);
            Assert.That(Action("marking-toggle", third), Is.True);
            Assert.That(Selected(), Is.EquivalentTo(choices));

            var profile = editor.Profile!;
            var reloaded = profile.Appearance.WithMarkings(profile.Appearance.Markings
                .Select(marking => Marking.ParseFromDbString(marking.ToString())!).ToList());
            saved = profile.WithCharacterAppearance(HumanoidCharacterAppearance.EnsureValid(reloaded, species, profile.Sex));
            Assert.That(saved.Appearance.Markings.Select(item => item.MarkingId).Where(choices.Contains), Is.EquivalentTo(choices));

            var sprite = pair.Client.EntMan.GetComponent<SpriteComponent>(editor.PreviewDummy);
            foreach (var id in choices)
            {
                var marking = prototypes.Index<MarkingPrototype>(id);
                var key = id + "-" + ((SpriteSpecifier.Rsi)marking.Sprites[0]).RsiState;
                Assert.That(sprite.LayerMapTryGet(key, out var index) && sprite[index].Visible, Is.True, id);
            }
            if (species == "Moth")
                Assert.That(sprite.LayerMapTryGet("MothAntennasDefault-default", out _), Is.False);

            Assert.That(Action("marking-combine", category), Is.True);
            Assert.That(Selected(), Is.EqualTo(new[] { third }));
            Assert.That(Action("marking-toggle", second), Is.True);
            Assert.That(Selected(), Is.EqualTo(new[] { second }));
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var dummy = entities.SpawnEntity("Mob" + species + "Dummy", MapCoordinates.Nullspace);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(dummy, saved);
            var appearance = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
            var selected = appearance.MarkingSet.GetForwardEnumerator().Select(item => item.MarkingId).ToArray();
            Assert.That(selected.Where(choices.Contains), Is.EquivalentTo(choices));
            if (species == "Moth")
                Assert.That(selected, Does.Not.Contain("MothAntennasDefault"));
            entities.DeleteEntity(dummy);
        });
        await pair.CleanReturnAsync();
    }
}
