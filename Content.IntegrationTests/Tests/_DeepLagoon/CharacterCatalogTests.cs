using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Preferences;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterCatalogTests
{
    [Test]
    public async Task CatalogEditsCurrentDraftAndRejectsStaleWindowActions()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), editor.CharacterSlot);
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"invalid\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"markings\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"hair\"}"), Is.True);
            var catalog = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            Assert.That(catalog.IsOpen, Is.True);
            using var state = JsonDocument.Parse(editor.CreateTguiCatalogState().ToString());
            Assert.That(state.RootElement.GetProperty("kind").GetString(), Is.EqualTo("hair"));
            var choices = state.RootElement.GetProperty("hairOptions").EnumerateArray().ToArray();
            Assert.That(choices.Length, Is.GreaterThan(4));
            var style = choices.First(choice => choice.GetProperty("id").GetString() != editor.Profile!.Appearance.HairStyleId)
                .GetProperty("id").GetString();
            var route = (Action<string, string>) typeof(TguiPanel).GetField("OnAction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog.Panel)!;
            route("hair-style", JsonSerializer.Serialize(new { value = style }));
            Assert.That(editor.Profile!.Appearance.HairStyleId, Is.EqualTo(style));
            Assert.That(editor.IsDirty, Is.True);
            route("name", "{\"value\":\"Rejected by catalog\"}");
            Assert.That(editor.Profile.Name, Is.Not.EqualTo("Rejected by catalog"));
            var staticData = typeof(TguiPanel).GetField("_staticData", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var catalogData = staticData.GetValue(catalog.Panel);
            Assert.That(editor.HandleTguiProfileAction("eye-color", "{\"value\":\"#12ef34\"}"), Is.True);
            typeof(HumanoidProfileEditor).GetMethod("FlushTguiProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.That(staticData.GetValue(catalog.Panel), Is.SameAs(catalogData), "Color edits must not resend static choices");
            var tabs = state.RootElement.GetProperty("tabs").EnumerateArray().ToArray();
            var markingsTab = tabs.First(tab => tab.GetProperty("mode").GetString() == "markings").GetProperty("id").GetInt32();
            Assert.That(editor.HandleTguiProfileAction("select-tab", JsonSerializer.Serialize(new { value = markingsTab })), Is.True);
            Assert.That(catalog.IsOpen, Is.False);
            route("hair-style", "{\"value\":\"HairBald\"}");
            Assert.That(editor.Profile.Appearance.HairStyleId, Is.EqualTo(style), "Closed catalog must not edit a draft");
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"markings\"}"), Is.True);
            catalog = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            route = (Action<string, string>) typeof(TguiPanel).GetField("OnAction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog.Panel)!;
            using var markings = JsonDocument.Parse(editor.CreateTguiCatalogState().ToString());
            Assert.That(markings.RootElement.GetProperty("markingOptions").EnumerateArray()
                .Any(option => option.GetProperty("id").GetString() == "HumanLongEars"), Is.True);
            route("marking-toggle", "{\"value\":\"HumanLongEars\"}");
            Assert.That(editor.Profile.Appearance.Markings.Any(marking => marking.MarkingId == "HumanLongEars"), Is.True);
            route("marking-toggle", "{\"value\":\"HumanLongEars\"}");
            Assert.That(editor.Profile.Appearance.Markings.Any(marking => marking.MarkingId == "HumanLongEars"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("marking-toggle", "{\"value\":\"NonexistentMarking\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("height", "{\"value\":1.1}"), Is.True);
            Assert.That(editor.HandleTguiProfileAction("width", "{\"value\":0.9}"), Is.True);
            Assert.That(editor.Profile.Appearance.Height, Is.EqualTo(1.1f));
            Assert.That(editor.Profile.Appearance.Width, Is.EqualTo(0.9f));
            var draft = editor.Profile;
            editor.SetProfile(draft, editor.CharacterSlot);
            Assert.That(catalog.IsOpen, Is.False, "Reloading a profile must close its catalog");
            route("marking-toggle", "{\"value\":\"HumanLongEars\"}");
            Assert.That(editor.Profile!.Appearance.Markings.Any(marking => marking.MarkingId == "HumanLongEars"), Is.False);
            Assert.That(editor.Profile.Appearance.Height, Is.EqualTo(1.1f));
            Assert.That(editor.Profile.Appearance.Width, Is.EqualTo(0.9f));
            var appearanceTab = tabs.First(tab => tab.GetProperty("mode").GetString() == "appearance").GetProperty("id").GetInt32();
            editor.HandleTguiProfileAction("select-tab", JsonSerializer.Serialize(new { value = appearanceTab }));
            var host = (TguiEditorHost) typeof(HumanoidProfileEditor)
                .GetField("_tguiHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            host.Measure(new Vector2(1100, 760));
            host.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(1100, 760)));
            Assert.That(host.Children.OfType<SpriteView>().Single().Position.X, Is.EqualTo(78));
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"beard\"}"), Is.True);
            catalog = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
            Assert.That(catalog.IsOpen, Is.False, "Closing the editor must close its catalog");

            lobby.OpenCharacterEditor();
            editor = (HumanoidProfileEditor) typeof(LobbyUIController)
                .GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), editor.CharacterSlot);
            draft = editor.Profile;
            var profilePanel = (TguiPanel) typeof(HumanoidProfileEditor)
                .GetField("_profileWeb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            host = (TguiEditorHost) typeof(HumanoidProfileEditor)
                .GetField("_tguiHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            Assert.That(editor.HandleTguiProfileAction("catalog-open", "{\"kind\":\"hair\"}"), Is.True);
            catalog = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            catalog.Panel.SwitchToNative();
            Assert.That(catalog.IsOpen, Is.False);
            Assert.That(profilePanel.UsingNative, Is.True);
            Assert.That(host.Parent, Is.Null);
            Assert.That(editor.Profile, Is.SameAs(draft), "Fallback must preserve the current draft");
            Assert.That(profilePanel.SwitchToTgui(), Is.True);
            Assert.That(profilePanel.UsingNative, Is.False);
            Assert.That(host.Parent, Is.Not.Null);
            Assert.That(editor.HandleTguiProfileAction("color-picker-open",
                "{\"id\":\"eye-color\",\"value\":\"#12ef34\"}"), Is.True);
            var colorPicker = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            colorPicker.Panel.SwitchToNative();
            Assert.That(colorPicker.IsOpen, Is.False);
            Assert.That(profilePanel.UsingNative, Is.True);
            Assert.That(host.Parent, Is.Null);
            Assert.That(editor.Profile, Is.SameAs(draft));
            Assert.That(profilePanel.SwitchToTgui(), Is.True);
            Assert.That(host.Parent, Is.Not.Null);
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}
