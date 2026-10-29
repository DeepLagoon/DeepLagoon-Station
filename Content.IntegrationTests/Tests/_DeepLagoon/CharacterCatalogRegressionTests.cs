using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared._DeepLagoon.Loadouts;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CharacterCatalogRegressionTests
{
    [Test]
    public async Task NativeBodyReturnsAfterRemovingReplacementAndGlowSurvivesProfileRoundTrip()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
            var system = entities.System<Content.Client.Humanoid.HumanoidAppearanceSystem>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("GeneticModification", prototypes);
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            var sprite = entities.GetComponent<SpriteComponent>(dummy);
            try
            {
                foreach (var (id, part) in new[] { ("SynthMechanicalLArm", HumanoidVisualLayers.LArm),
                    ("SynthMechanicalLLeg", HumanoidVisualLayers.LLeg), ("SynthMechanicalHead", HumanoidVisualLayers.Head),
                    ("SynthMechanicalChest", HumanoidVisualLayers.Chest), ("SynthMechanicalLHand", HumanoidVisualLayers.LHand),
                    ("SynthMechanicalLFoot", HumanoidVisualLayers.LFoot) })
                {
                    var customized = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new() { prototypes.Index<MarkingPrototype>(id).AsMarking() }));
                    system.LoadProfile(dummy, customized);
                    Assert.That(sprite[sprite.LayerMapGet(part)].Visible, Is.False, id);
                    var humanoid = entities.GetComponent<HumanoidAppearanceComponent>(dummy);
                    var dirty = false;
                    system.SetLayerVisibility((dummy, humanoid), part, false, SlotFlags.MASK, ref dirty);
                    system.SetLayerVisibility((dummy, humanoid), part, true, SlotFlags.MASK, ref dirty);
                    Assert.That(sprite[sprite.LayerMapGet(part)].Visible, Is.False, "Removing clothing must not reveal the base under " + id);
                    system.LoadProfile(dummy, profile);
                    Assert.That(sprite[sprite.LayerMapGet(part)].Visible, Is.True, "Native part returns after removing " + id);
                }
                var marking = prototypes.Index<MarkingPrototype>("SynthAccessoryHornsLights").AsMarking();
                marking.Unshaded = true;
                marking.DrawOrder = 7;
                var stored = Marking.ParseFromDbString(marking.ToString())!;
                Assert.That(stored, Is.EqualTo(marking));
                Assert.That(new Marking(stored).Unshaded, Is.True);
                var glowing = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new() { stored }));
                system.LoadProfile(dummy, glowing);
                Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(dummy).MarkingSet.Markings.Values.SelectMany(list => list)
                    .Single(m => m.MarkingId == marking.MarkingId).Unshaded, Is.True);
                foreach (var spec in prototypes.Index<MarkingPrototype>(marking.MarkingId).Sprites.OfType<Robust.Shared.Utility.SpriteSpecifier.Rsi>())
                    Assert.That(((SpriteComponent.Layer)sprite[sprite.LayerMapGet(marking.MarkingId + "-" + spec.RsiState)]).ShaderPrototype?.Id, Is.EqualTo("unshaded"));
                Assert.That(Marking.ParseFromDbString("SynthAccessoryHornsLights@#FFFFFFFF")!.Unshaded, Is.False);
            }
            finally { entities.DeleteEntity(dummy); }
        });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Synth");
            var marking = prototypes.Index<MarkingPrototype>("SynthAccessoryHornsLights").AsMarking();
            marking.Unshaded = true;
            profile = profile.WithCharacterAppearance(profile.Appearance.WithMarkings(new() { marking }));
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(dummy, profile);
            Assert.That(entities.GetComponent<HumanoidAppearanceComponent>(dummy).MarkingSet.Markings.Values.SelectMany(list => list)
                .Single(m => m.MarkingId == marking.MarkingId).Unshaded, Is.True);
            entities.DeleteEntity(dummy);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EquippedPaintUpdatesWhenAppearanceArrivesAfterEquipment()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        EntityUid dummy = default, item = default;
        var tint = Color.FromHex("#123456");
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            item = entities.SpawnEntity("ClothingNeckCloakCap", MapCoordinates.Nullspace);
            Assert.That(entities.System<InventorySystem>().TryEquip(dummy, item, "neck", force: true), Is.True);
            entities.EnsureComponent<PersonalLoadoutVisualsComponent>(item);
            entities.EnsureComponent<AppearanceComponent>(item);
            entities.System<AppearanceSystem>().SetData(item, PersonalLoadoutVisuals.Color, tint);
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            var keys = entities.GetComponent<Content.Client.Inventory.InventorySlotsComponent>(dummy).VisualLayerKeys["neck"];
            Assert.That(keys, Is.Not.Empty);
            var sprite = entities.GetComponent<SpriteComponent>(dummy);
            foreach (var key in keys) Assert.That(sprite[sprite.LayerMapGet(key)].Color, Is.EqualTo(tint));
            entities.DeleteEntity(dummy);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PreviewArousalAndCategoryCombinationRemainIndependent()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Synth"), editor.CharacterSlot);
            bool Action(string action, object args) => editor.HandleTguiProfileAction(action, JsonSerializer.Serialize(args));
            Assert.That(Action("marking-combine", new { value = "Arms" }), Is.True);
            Assert.That(Action("marking-combine", new { value = "Hands" }), Is.True);
            Assert.That(Action("marking-combine", new { value = "Arms" }), Is.True);
            using (var state = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString()))
                Assert.That(state.RootElement.GetProperty("combinedMarkingCategories").EnumerateArray().Select(category => category.GetProperty("id").GetString()), Is.EqualTo(new[] { "Hands" }));
            Assert.That(Action("body-feature", new { part = "penis", field = "enabled", value = "true" }), Is.True);
            var profile = editor.Profile!;
            Assert.That(Action("preview-arousal", new { }), Is.True);
            Assert.That(pair.Client.EntMan.GetComponent<HumanoidAppearanceComponent>(editor.PreviewDummy).GenitalAroused, Is.True);
            Assert.That(editor.Profile, Is.SameAs(profile));
            Assert.That(Action("marking-add", new { value = "SynthAccessoryHornsLights" }), Is.True);
            Assert.That(pair.Client.EntMan.GetComponent<HumanoidAppearanceComponent>(editor.PreviewDummy).GenitalAroused, Is.True);
            var index = editor.Profile!.Appearance.Markings.FindIndex(m => m.MarkingId == "SynthAccessoryHornsLights");
            Assert.That(Action("marking-glow", new { index }), Is.True);
            Assert.That(editor.Profile.Appearance.Markings[index].Unshaded, Is.True);
            Assert.That(Action("preview-arousal", new { }), Is.True);
            Assert.That(pair.Client.EntMan.GetComponent<HumanoidAppearanceComponent>(editor.PreviewDummy).GenitalAroused, Is.False);
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task ClothWrapsEquipInBothSlotsThroughTheEditorAndSpawnSystem()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var profile = HumanoidCharacterProfile.DefaultWithSpecies("Human");
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Client.EntMan;
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            using var editor = new Content.Client.Lobby.UI.Loadouts.PersonalLoadoutEditor { TguiMode = true };
            editor.ProfileChanged += changed => profile = changed;
            foreach (var (slot, id) in new[] { ("gloves", "DLEELoadoutHandsClothWrap"), ("shoes", "DLEELoadoutShoesClothWrap") })
            {
                editor.Refresh(profile, "Passenger", null);
                Assert.That(editor.SelectTguiSlot(dummy, slot), Is.True);
                Content.Client._DeepLagoon.WebUI.TguiActionData.TryParse(JsonSerializer.Serialize(new { id }), out var args);
                Assert.That(editor.HandleTguiAction("select", args), Is.True);
            }
            Assert.That(entities.System<PersonalLoadoutSystem>().GetSelections(profile).Count(), Is.EqualTo(2));
            Assert.That(entities.System<PersonalLoadoutEquipSystem>().Apply(dummy, profile, "Passenger", null, preview: true), Is.Empty);
            var sprite = entities.GetComponent<SpriteComponent>(dummy);
            var keys = entities.GetComponent<Content.Client.Inventory.InventorySlotsComponent>(dummy).VisualLayerKeys;
            foreach (var slot in new[] { "gloves", "shoes" })
            {
                Assert.That(keys[slot], Is.Not.Empty);
                Assert.That(keys[slot].Any(key => sprite[sprite.LayerMapGet(key)].Visible), Is.True, slot);
            }
            entities.DeleteEntity(dummy);
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(CCVars.PersonalLoadoutsEnabled, true);
            var entities = pair.Server.EntMan;
            var dummy = entities.SpawnEntity("MobHumanDummy", MapCoordinates.Nullspace);
            Assert.That(entities.System<PersonalLoadoutEquipSystem>().Apply(dummy, profile, "Passenger", null), Is.Empty);
            var inventory = entities.System<InventorySystem>();
            Assert.That(inventory.TryGetSlotEntity(dummy, "gloves", out var gloves), Is.True);
            Assert.That(inventory.TryGetSlotEntity(dummy, "shoes", out var shoes), Is.True);
            Assert.That(gloves, Is.Not.EqualTo(shoes));
            entities.DeleteEntity(dummy);
        });
        await pair.CleanReturnAsync();
    }

}
