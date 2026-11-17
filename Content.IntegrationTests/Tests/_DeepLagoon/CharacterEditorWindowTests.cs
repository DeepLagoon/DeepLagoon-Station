using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Robust.Client.UserInterface;
using Robust.Shared.ContentPack;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterEditorWindowTests
{
    [Test]
    public async Task RemovingPreviewClothingUpdatesDraftAndRebuildsPreview()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>()
                .SetCVar(Content.Shared.CCVar.CCVars.PersonalLoadoutsEnabled, true);
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var window = ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single();
            var editor = (HumanoidProfileEditor)typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            const string personalId = "DLEELoadoutClothingJumpsuitSuitBlack";
            Assert.That(editor.HandleTguiProfileAction("equipment/select", JsonSerializer.Serialize(new { id = personalId })), Is.True);
            var entities = pair.Client.ResolveDependency<Robust.Shared.GameObjects.IEntityManager>();
            var inventory = entities.System<Content.Shared.Inventory.InventorySystem>();
            var personal = entities.System<Content.Shared._DeepLagoon.Loadouts.PersonalLoadoutSystem>();
            Assert.That(inventory.TryGetSlotEntity(editor.PreviewDummy, "jumpsuit", out var worn), Is.True);
            var before = editor.PreviewDummy;
            var wornPrototype = entities.GetComponent<Robust.Shared.GameObjects.MetaDataComponent>(worn!.Value).EntityPrototype!.ID;
            Assert.That(personal.GetSelections(editor.Profile!).Any(choice => choice.Prototype.Id == personalId), Is.True);
            Assert.That(editor.HandleTguiProfileAction("preview-slot-remove", "{\"value\":\"not-a-slot\"}"), Is.False);
            Assert.That(editor.HandleTguiProfileAction("preview-slot-remove", "{\"value\":\"jumpsuit\"}"), Is.True);
            Assert.That(personal.GetSelections(editor.Profile!).Any(choice => choice.Prototype.Id == personalId), Is.False);
            Assert.That(editor.IsDirty, Is.True);
            Assert.That(editor.PreviewDummy, Is.Not.EqualTo(before));
            if (inventory.TryGetSlotEntity(editor.PreviewDummy, "jumpsuit", out var after))
                Assert.That(entities.GetComponent<Robust.Shared.GameObjects.MetaDataComponent>(after!.Value).EntityPrototype!.ID, Is.Not.EqualTo(wornPrototype));
            editor.ResetToDefault();
            window.CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PreviewSlotsExposeUnderwearAndFocusTheMatchingCatalogueCategory()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>()
                .SetCVar(Content.Shared.CCVar.CCVars.PersonalLoadoutsEnabled, true);
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var window = ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single();
            var editor = (HumanoidProfileEditor)typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;

            // The editor's slot column must offer the new underwear slots with their own icons.
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("equipment").ToString()))
            {
                var slots = state.RootElement.GetProperty("previewSlots").EnumerateArray()
                    .Select(slot => slot.GetProperty("id").GetString()).ToArray();
                var missing = new[] { "underwearbottom", "underweartop", "socks" }.Where(id => !slots.Contains(id)).ToArray();
                Assert.That(missing, Is.Empty, $"editor slots: {string.Join(',', slots)}");
                Assert.That(state.RootElement.GetProperty("previewSlots").EnumerateArray()
                    .Where(slot => slot.GetProperty("id").GetString() is "underwearbottom" or "underweartop" or "socks")
                    .All(slot => slot.GetProperty("background").GetString() is { Length: > 0 }), Is.True,
                    "each underwear slot needs its own strip icon");
            }

            // Clicking a slot in the editor must focus the catalogue category that fits it.
            Assert.That(editor.HandleTguiProfileAction("preview-slot", "{\"value\":\"underwearbottom\"}"), Is.True);
            int focus;
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("equipment").ToString()))
            {
                var equipment = state.RootElement.GetProperty("equipment");
                Assert.That(equipment.GetProperty("slotCategory").GetString(), Is.EqualTo("UnderwearBottom"));
                focus = equipment.GetProperty("slotFocus").GetInt32();
                Assert.That(focus, Is.GreaterThanOrEqualTo(1), "a slot click must ask the browser to focus its category");
                var items = equipment.GetProperty("items").EnumerateArray().ToArray();
                Assert.That(items, Is.Not.Empty, "the underwear slot must offer garments");
                Assert.That(items.Select(item => item.GetProperty("category").GetString()), Is.All.EqualTo("UnderwearBottom"));
            }

            // Selecting one of them must resolve its personal group and reach the preview dummy.
            const string bottom = "DLUnderwearClothingUnderBottomGrey";
            Assert.That(editor.HandleTguiProfileAction("equipment/select", JsonSerializer.Serialize(new { id = bottom })), Is.True);
            var entities = pair.Client.ResolveDependency<Robust.Shared.GameObjects.IEntityManager>();
            var inventory = entities.System<Content.Shared.Inventory.InventorySystem>();
            Assert.That(inventory.TryGetSlotEntity(editor.PreviewDummy, "underwearbottom", out var worn), Is.True);
            Assert.That(entities.GetComponent<Robust.Shared.GameObjects.MetaDataComponent>(worn!.Value).EntityPrototype!.ID,
                Is.EqualTo("ClothingUnderBottomGrey"));

            Assert.That(editor.HandleTguiProfileAction("preview-slot", "{\"value\":\"socks\"}"), Is.True);
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("equipment").ToString()))
            {
                var equipment = state.RootElement.GetProperty("equipment");
                Assert.That(equipment.GetProperty("slotCategory").GetString(), Is.EqualTo("UnderwearSocks"));
                Assert.That(equipment.GetProperty("slotFocus").GetInt32(), Is.GreaterThan(focus));
                Assert.That(equipment.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("category").GetString()),
                    Is.All.EqualTo("UnderwearSocks"));
            }

            editor.ResetToDefault();
            window.CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FloatingEditorKeepsDraftAndIsolatesAppearanceFromChat()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var window = ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single();
            Assert.That(window.IsOpen, Is.True);
            var editor = (HumanoidProfileEditor)typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            var preferences = pair.Client.ResolveDependency<IClientPreferencesManager>();
            var chatBefore = preferences.Preferences!.ChatPanelSettings;
            var settings = JsonDocument.Parse(editor.CreateTguiProfileState("settings").ToString());
            Assert.That(settings.RootElement.GetProperty("tabs").EnumerateArray().Any(tab => tab.GetProperty("mode").GetString() == "settings"), Is.True);
            Assert.That(settings.RootElement.GetProperty("previewFloor").GetString(), Does.StartWith("data:image/png;base64,"));

            var host = (TguiEditorHost)typeof(HumanoidProfileEditor).GetField("_tguiHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var sprite = host.Children.OfType<Robust.Client.UserInterface.Controls.SpriteView>().Single();
            foreach (var direction in new[] { Robust.Shared.Maths.Direction.West, Robust.Shared.Maths.Direction.North, Robust.Shared.Maths.Direction.East, Robust.Shared.Maths.Direction.South })
            {
                Assert.That(editor.HandleTguiProfileAction("rotate", "{\"value\":-1}"), Is.True);
                Assert.That(sprite.OverrideDirection, Is.EqualTo(direction));
            }

            Assert.That(editor.HandleTguiProfileAction("name", "{\"value\":\"Семён Ёлкин\"}"), Is.True);
            var draft = editor.Profile;
            lobby.OpenCharacterEditor();
            Assert.That(editor.Profile, Is.SameAs(draft), "Opening the existing window must not discard a draft");
            Assert.That(editor.HandleTguiProfileAction("appearance-setting", "{\"field\":\"chatBgColor\",\"value\":\"#123456\"}"), Is.True);
            Assert.That(editor.Profile, Is.SameAs(draft));
            Assert.That(preferences.Preferences.ChatPanelSettings, Is.EqualTo(chatBefore));
            var appearance = new CharacterEditorAppearance();
            appearance.Load(pair.Client.ResolveDependency<IResourceManager>());
            using var stored = JsonDocument.Parse(appearance.Data().ToString());
            Assert.That(stored.RootElement.GetProperty("chatBgColor").GetString(), Is.EqualTo("#123456"));
            editor.HandleTguiProfileAction("headshot-library", "{}");
            var library = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            Assert.That(library.IsOpen, Is.True);
            Assert.That(typeof(Content.Client._DeepLagoon.WebUI.TguiPanel).GetField("_inheritChatAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(library.Panel), Is.False);
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("flavor").ToString()))
                Assert.That(state.RootElement.GetProperty("libraryOpen").GetBoolean(), Is.True);
            editor.HandleTguiProfileAction("headshot-library", "{}");
            Assert.That(library.IsOpen, Is.False);
            editor.HandleTguiProfileAction("headshot-library", "{}");
            library = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            library.Close();
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("flavor").ToString()))
                Assert.That(state.RootElement.GetProperty("libraryOpen").GetBoolean(), Is.False);

            window.Measure(new Vector2(1220, 860));
            window.Arrange(new Robust.Shared.Maths.UIBox2(Vector2.Zero, new Vector2(1220, 860)));
            var drag = typeof(CharacterEditorWindow).GetMethod("GetDragModeFor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.That(drag.Invoke(window, [new Vector2(window.Size.X - 2, window.Size.Y - 2)])!.ToString(), Does.Contain("Right").And.Contain("Bottom"));
            Assert.That(drag.Invoke(window, [window.Size / 2])!.ToString(), Is.EqualTo("None"));

            Assert.That(editor.HandleTguiProfileAction("appearance-reset", "{}"), Is.True);

            window.Close();
            Assert.That(window.IsOpen, Is.True, "Dirty close must leave the editor open for the save/discard decision");
            var savePanel = ui.WindowRoot.Children.OfType<CharacterSetupGuiSavePanel>().Single();
            Assert.That(savePanel.IsOpen, Is.True);
            Assert.That(savePanel, Is.Not.InstanceOf<Robust.Client.UserInterface.CustomControls.DefaultWindow>());
            Assert.That(typeof(Content.Client._DeepLagoon.WebUI.TguiPanel).GetField("_inheritChatAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(savePanel.Panel), Is.False);
            savePanel.Close();
            Assert.That(window.IsOpen, Is.True, "Cancel leaves the draft open");
            editor.HandleTguiProfileAction("headshot-library", "{}");
            library = ui.WindowRoot.Children.OfType<CharacterAuxiliaryWindow>().Single();
            editor.HandleTguiProfileAction("reset", "{}");
            window.Close();
            Assert.That(window.IsOpen, Is.False);
            Assert.That(library.IsOpen, Is.False, "Closing the editor also closes its library");
            lobby.OpenCharacterEditor();
            Assert.That(ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single(), Is.SameAs(window));
            window.CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}

