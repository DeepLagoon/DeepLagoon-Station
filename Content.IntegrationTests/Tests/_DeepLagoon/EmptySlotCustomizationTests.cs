#nullable enable
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class EmptySlotCustomizationTests
{
    [Test]
    public async Task RealEditorKeepsUnselectedItemCustomizationWithEmptySlot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitPost(() => pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true));
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor)typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            var preferences = pair.Client.ResolveDependency<IClientPreferencesManager>();
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), preferences.Preferences!.SelectedCharacterIndex);
            _ = editor.CreateTguiProfileState("equipment");
            Assert.That(editor.HandleTguiProfileAction("equipment/job", "{\"value\":\"Contractor\"}"), Is.True);
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var inventory = entities.System<InventorySystem>();
            JsonElement Catalog(string list, string id)
            {
                using var document = JsonDocument.Parse(editor.CreateTguiProfileState("equipment").ToString());
                return document.RootElement.GetProperty("equipment").GetProperty(list).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).Clone();
            }
            void Customize(string list, string id, string? group, bool paid, string slot)
            {
                Assert.That(inventory.TryGetSlotEntity(editor.PreviewDummy, slot, out _), Is.False, "The target slot must start empty");
                Assert.That(editor.HandleTguiProfileAction("equipment/paint", JsonSerializer.Serialize(new { id, group, jobItem = paid, value = "#123456" })), Is.True);
                Assert.That(Catalog(list, id).GetProperty("color").GetString(), Is.EqualTo("#123456FF"));
                var dummy = editor.PreviewDummy;
                Assert.That(editor.HandleTguiProfileAction("equipment/rename", JsonSerializer.Serialize(new { id, group, jobItem = paid, name = "Мой предмет", description = "Моё описание" })), Is.True);
                Assert.That(editor.PreviewDummy, Is.EqualTo(dummy), "The second edit must exercise the reused preview path");
                var item = Catalog(list, id);
                Assert.That(item.GetProperty("selected").GetBoolean(), Is.False);
                Assert.That(item.GetProperty("color").GetString(), Is.EqualTo("#123456FF"), "Renaming must preserve the preceding paint action");
                Assert.That(item.GetProperty("customName").GetString(), Is.EqualTo("Мой предмет"));
                Assert.That(item.GetProperty("description").GetString(), Is.EqualTo("Моё описание"));
                Assert.That(inventory.TryGetSlotEntity(editor.PreviewDummy, slot, out _), Is.False);
                Assert.That(editor.HandleTguiProfileAction(paid ? "equipment/job-select" : "equipment/select", JsonSerializer.Serialize(new { id, group })), Is.True);
                Assert.That(inventory.TryGetSlotEntity(editor.PreviewDummy, slot, out var worn), Is.True);
                Assert.That(entities.GetComponent<MetaDataComponent>(worn!.Value).EntityName, Is.EqualTo("Мой предмет"));
                Assert.That(entities.GetComponent<MetaDataComponent>(worn.Value).EntityDescription, Is.EqualTo("Моё описание"));
                Assert.That(entities.System<SharedAppearanceSystem>().TryGetData<Color>(worn.Value, PersonalLoadoutVisuals.Color, out var color), Is.True);
                Assert.That(color, Is.EqualTo(Color.FromHex("#123456")));
            }
            using var catalogue = JsonDocument.Parse(editor.CreateTguiProfileState("equipment").ToString());
            var paidItem = catalogue.RootElement.GetProperty("equipment").GetProperty("jobItems").EnumerateArray()
                .First(item => item.GetProperty("category").GetString() == "Hands" && item.GetProperty("canSelect").GetBoolean() && !item.GetProperty("selected").GetBoolean());
            Customize("jobItems", paidItem.GetProperty("id").GetString()!, paidItem.GetProperty("group").GetString(), true, "gloves");
            const string personalId = "DLEELoadoutClothingJumpsuitSuitBlack";
            // Clear the uniform slot through the actual editor actions, including cross-currency conflicts.
            editor.HandleTguiProfileAction("equipment/select", JsonSerializer.Serialize(new { id = personalId }));
            editor.HandleTguiProfileAction("equipment/select", JsonSerializer.Serialize(new { id = personalId }));
            Customize("items", personalId, null, false, "jumpsuit");
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}
