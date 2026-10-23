using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Preferences;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using System.Numerics;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterAppearanceLayoutTests
{
    [Test]
    public async Task AppearanceHidesClothesAndOwnsItsFloatingColorWindow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Synth"), editor.CharacterSlot);
            using var initial = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            var clothes = initial.RootElement.GetProperty("showClothes").GetBoolean();
            var tabs = initial.RootElement.GetProperty("tabs").EnumerateArray().ToArray();
            var markingsTab = tabs.First(t => t.GetProperty("mode").GetString() == "markings").GetProperty("id").GetInt32();
            Assert.That(editor.HandleTguiProfileAction("select-tab", JsonSerializer.Serialize(new { value = markingsTab })), Is.True);
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            Assert.That(state.RootElement.GetProperty("showClothes").GetBoolean(), Is.False);
            Assert.That(editor.HandleTguiProfileAction("clothes", "{}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("preview-bounds", "{\"x\":200,\"y\":160,\"width\":232,\"height\":320}"), Is.True);
            var host = (TguiEditorHost) typeof(HumanoidProfileEditor)
                .GetField("_tguiHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            host.Measure(new Vector2(1000, 800));
            host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(1000, 800)));
            var preview = host.Children.OfType<SpriteView>().Single();
            Assert.That(preview.Position, Is.EqualTo(new Vector2(200, 160)));
            Assert.That(editor.HandleTguiProfileAction("preview-bounds", "{\"x\":150,\"y\":160,\"width\":112,\"height\":210}"), Is.True);
            host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(420, 800)));
            Assert.That(preview.Position, Is.EqualTo(new Vector2(150, 160)));
            Assert.That(preview.Size, Is.EqualTo(new Vector2(112, 210)));
            Assert.That(editor.HandleTguiProfileAction("color-picker-open", "{\"id\":\"test-color\",\"title\":\"Цвет\",\"value\":\"#ff0000\"}"), Is.True);
            Assert.That(ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single().IsOpen, Is.True);
            Assert.That(editor.HandleTguiProfileAction("color-picker-change", "{\"id\":\"stale\",\"value\":\"#00ff00\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("color-picker-change", "{\"id\":\"test-color\",\"value\":\"#00ff00\"}"), Is.True);
            using var changed = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            Assert.That(changed.RootElement.GetProperty("colorPickerResult").GetProperty("sequence").GetInt32(), Is.GreaterThan(0));
            Assert.That(editor.HandleTguiProfileAction("select-tab", "{\"value\":0}"), Is.True);
            Assert.That(ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>(), Is.Empty);
            using var restored = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            Assert.That(restored.RootElement.GetProperty("showClothes").GetBoolean(), Is.EqualTo(clothes));
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        await pair.CleanReturnAsync();
    }
}
