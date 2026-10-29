using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Client.Humanoid;
using Content.Client.Wires.UI;
using Content.Server.Research.Systems;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.CCVar;
using Content.Shared.Inventory;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Wires;
using Moq;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BugreportRegressionTests
{
    [Test]
    public async Task TguiClothingSelectionRemembersAndMovesTheChosenNeckSlot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Client.EntMan;
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            using var editor = new Content.Client.Lobby.UI.Loadouts.PersonalLoadoutEditor { TguiMode = true };
            editor.ProfileChanged += changed => profile = changed;
            try
            {
                const string id = "DLEELoadoutNeckScarfStripedRed";
                void Select(string slot)
                {
                    editor.Refresh(profile, "Passenger", null);
                    Assert.That(editor.SelectTguiSlot(dummy, slot), Is.True);
                    Assert.That(Content.Client._DeepLagoon.WebUI.TguiActionData.TryParse("{\"id\":\"" + id + "\"}", out var args), Is.True);
                    Assert.That(editor.HandleTguiAction("select", args), Is.True);
                }
                Select("neck2");
                Assert.That(entities.System<PersonalLoadoutSystem>().GetSelections(profile).Single().Customization.Slot, Is.EqualTo("neck2"));
                Select("neck3");
                Assert.That(entities.System<PersonalLoadoutSystem>().GetSelections(profile).Single().Customization.Slot, Is.EqualTo("neck3"));
                editor.Refresh(profile, "Passenger", null);
                Content.Client._DeepLagoon.WebUI.TguiActionData.TryParse("{\"id\":\"" + id + "\",\"value\":\"#123456\"}", out var paint);
                Assert.That(editor.HandleTguiAction("paint", paint), Is.True);
                var custom = entities.System<PersonalLoadoutSystem>().GetSelections(profile).Single().Customization;
                Assert.That(custom.Slot, Is.EqualTo("neck3"));
                Assert.That(custom.Color, Is.Not.Null);
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NativeWireLayoutAndCloakLayerOrder()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var menu = new WiresMenu();
            menu.Populate(new WiresBoundUserInterfaceState([new ClientWire(0, false, WireColor.Red, WireLetter.α)], [], "Test", "Test", 0));
            menu.Measure(new Vector2(400, 300));
            menu.Arrange(UIBox2.FromDimensions(Vector2.Zero, new Vector2(400, 300)));
            IEnumerable<Control> Controls(Control root) => root.Children.SelectMany(child => Controls(child).Prepend(child));
            var wire = Controls(menu).Single(control => control.GetType().Name == "WireRender");
            Assert.That(wire.Size.X, Is.GreaterThan(0));
            Assert.That(wire.Size.Y, Is.GreaterThan(0));
            var entities = pair.Client.EntMan;
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            try
            {
                var appearance = entities.System<HumanoidAppearanceSystem>();
                var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                appearance.LoadProfile(dummy, profile);
                var sprite = entities.GetComponent<Robust.Client.GameObjects.SpriteComponent>(dummy);
                var inventory = entities.System<InventorySystem>();
                foreach (var slot in new[] { "neck", "neck2", "neck3" })
                {
                    var cloak = entities.SpawnEntity("ClothingNeckCloakCap", MapCoordinates.Nullspace);
                    Assert.That(inventory.TryEquip(dummy, cloak, slot, force: true), Is.True);
                }
                appearance.LoadProfile(dummy, profile);
                var keys = entities.GetComponent<Content.Client.Inventory.InventorySlotsComponent>(dummy).VisualLayerKeys;
                Assert.That(sprite.LayerMapTryGet(HumanoidVisualLayers.Hair, out var hair), Is.True);
                foreach (var slot in new[] { "neck", "neck2", "neck3" })
                foreach (var key in keys[slot])
                {
                    Assert.That(sprite.LayerMapTryGet(key, out var layer), Is.True);
                    Assert.That(layer, Is.LessThan(hair), $"{slot}: {key} must be below hair");
                }
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FileDialogUploadTransfersImageInMultipleChunks()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        using var picture = new Image<Rgba32>(128, 128);
        var random = new Random(42);
        for (var y = 0; y < picture.Height; y++)
        for (var x = 0; x < picture.Width; x++)
            picture[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var png = new MemoryStream();
        picture.SaveAsPng(png);
        var bytes = png.ToArray();
        Assert.That(bytes.Length, Is.GreaterThan(Content.Shared._DeepLagoon.CharacterInfo.HeadshotLimits.ChunkBytes));
        var dialogs = new Mock<IFileDialogManager>();
        dialogs.Setup(d => d.OpenFile(It.IsAny<FileDialogFilters?>(), FileAccess.Read, It.IsAny<FileShare?>()))
            .ReturnsAsync(new MemoryStream(bytes));
        var system = pair.Client.EntMan.System<Content.Client._DeepLagoon.CharacterInfo.HeadshotSystem>();
        var field = system.GetType().GetField("_dialogs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(system);
        byte[] received = null;
        string error = null;
        try
        {
            await pair.Server.WaitPost(() =>
            {
                var session = pair.Server.PlayerMan.Sessions.Single();
                if (pair.Server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().GetAdminData(session, true) is { } admin)
                    admin.Flags &= ~Content.Shared.Administration.AdminFlags.Host;
            });
            await pair.Client.WaitPost(() =>
            {
                field.SetValue(system, dialogs.Object);
                var slot = pair.Client.ResolveDependency<Content.Client.Lobby.IClientPreferencesManager>().Preferences.SelectedCharacterIndex;
                system.Upload(slot, (data, mime, id, message) => { received = data; error = message; });
            });
            await pair.RunTicksSync(200);
            Assert.That(error, Is.Empty);
            Assert.That(received, Is.Not.Empty);
            Assert.That(received, Is.EqualTo(Content.Server._DeepLagoon.CharacterInfo.HeadshotImage.Normalize(bytes, false).Bytes));
        }
        finally { await pair.Client.WaitPost(() => field.SetValue(system, previous)); }
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DisconnectingResearchClientPreservesServerAndOtherClients()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var research = entities.System<ResearchSystem>();
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var server = entities.SpawnEntity("ResearchAndDevelopmentServer", MapCoordinates.Nullspace);
            var first = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var second = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.AddComponent<ResearchClientComponent>(first);
            entities.AddComponent<TechnologyDatabaseComponent>(first);
            entities.AddComponent<ResearchClientComponent>(second);
            entities.AddComponent<TechnologyDatabaseComponent>(second);
            var database = entities.GetComponent<TechnologyDatabaseComponent>(server);
            var disciplines = new List<ProtoId<TechDisciplinePrototype>>(database.SupportedDisciplines);
            Assert.That(disciplines, Is.Not.Empty);
            research.RegisterClient(first, server);
            research.RegisterClient(second, server);
            research.UnregisterClient(first);
            Assert.That(database.SupportedDisciplines, Is.EqualTo(disciplines));
            Assert.That(entities.GetComponent<TechnologyDatabaseComponent>(second).SupportedDisciplines, Is.EqualTo(disciplines));
            Assert.That(entities.GetComponent<TechnologyDatabaseComponent>(first).SupportedDisciplines, Is.Empty);
            research.RegisterClient(first, server);
            var technology = prototypes.EnumeratePrototypes<TechnologyPrototype>()
                .First(t => t.GenericUnlocks.Count == 0 && t.RecipeUnlocks.Count > 0 && disciplines.Contains(t.Discipline));
            research.AddTechnology(server, technology);
            foreach (var client in new[] { first, second })
            {
                var copy = entities.GetComponent<TechnologyDatabaseComponent>(client);
                Assert.That(copy.UnlockedTechnologies, Does.Contain((ProtoId<TechnologyPrototype>)technology.ID));
                Assert.That(copy.UnlockedRecipes, Is.EquivalentTo(database.UnlockedRecipes));
            }
            entities.DeleteEntity(first);
            Assert.That(database.SupportedDisciplines, Is.EqualTo(disciplines));
            Assert.That(database.UnlockedTechnologies, Does.Contain((ProtoId<TechnologyPrototype>)technology.ID));
            entities.DeleteEntity(second);
            entities.DeleteEntity(server);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PersonalNeckClothesUseTheirSelectedSlots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var selections = new[] { "DLEELoadoutNeckScarfStripedRed", "DLEELoadoutNeckScarfStripedBlue", "DLEELoadoutNeckScarfStripedGreen" };
            var slots = new[] { "neck3", "neck", "neck2" };
            var role = new RoleLoadout(PersonalLoadoutSystem.Role);
            role.SelectedLoadouts["DLEinsteinLoadoutNeck"] = selections.Select((id, i) => new Loadout
            {
                Prototype = id, Customization = new PersonalLoadoutCustomization { Slot = slots[i] },
            }).ToList();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithLoadout(role);
            var character = entities.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var inventory = entities.System<InventorySystem>();
            var loadouts = entities.System<PersonalLoadoutSystem>();
            try
            {
                entities.System<PersonalLoadoutEquipSystem>().Apply(character, profile, "Passenger", null, true);
                for (var i = 0; i < slots.Length; i++)
                {
                    Assert.That(inventory.TryGetSlotEntity(character, slots[i], out var item), Is.True, slots[i]);
                    Assert.That(entities.GetComponent<MetaDataComponent>(item!.Value).EntityPrototype!.ID,
                        Is.EqualTo(prototypes.Index<LoadoutPrototype>(selections[i]).PersonalItems[0].Id));
                }
                var updated = role.Clone();
                var replacement = prototypes.Index<LoadoutPrototype>("DLEELoadoutNeckScarfStripedPurple");
                loadouts.RemovePersonalSlotConflicts(updated, profile, replacement, "neck2");
                Assert.That(updated.SelectedLoadouts.Values.SelectMany(x => x).Select(x => x.Prototype.Id),
                    Is.EquivalentTo(selections.Take(2)), "Replacing neck2 must preserve neck and neck3");
                var custom = new PersonalLoadoutCustomization { Slot = "neck3", Color = "#123456" };
                var json = System.Text.Json.JsonSerializer.Serialize(custom);
                Assert.That(System.Text.Json.JsonSerializer.Deserialize<PersonalLoadoutCustomization>(json), Is.EqualTo(custom));
                var catalogue = loadouts.Catalogue(profile, "Passenger", null).ToArray();
                Assert.That(catalogue.Count(item => item.PersonalItems.Count == 1 && item.PersonalItems[0].Id == "ClothingNeckScarfStripedPurple"), Is.EqualTo(1));
                Assert.That(loadouts.PersonalSlot(profile, prototypes.Index<LoadoutPrototype>("DLEELoadoutHandsClothWrap")), Is.EqualTo("gloves"));
                Assert.That(loadouts.PersonalSlot(profile, prototypes.Index<LoadoutPrototype>("DLEELoadoutShoesClothWrap")), Is.EqualTo("shoes"));
            }
            finally { entities.DeleteEntity(character); }
        });
        await pair.CleanReturnAsync();
    }
}
