using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Humanoid;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BodyCustomizationEditorTests
{
    [Test]
    public async Task StraightLegCheckboxUpdatesDraftAndPreviewForSupportedSpecies()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            foreach (var species in new[] { "Vulpkanin", "Feroxi", "Tajaran" })
            {
                var saved = HumanoidCharacterProfile.DefaultWithSpecies(species);
                editor.SetProfile(saved, editor.CharacterSlot);
                using (var initial = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString()))
                {
                    Assert.That(initial.RootElement.GetProperty("canUsePlantigradeLegs").GetBoolean(), Is.True);
                    Assert.That(initial.RootElement.GetProperty("plantigradeLegs").GetBoolean(), Is.False);
                }
                Assert.That(editor.HandleTguiProfileAction("plantigrade-legs", "{\"value\":true}"), Is.True);
                Assert.That(editor.Profile!.Appearance.PlantigradeLegs, Is.True);
                Assert.That(saved.Appearance.PlantigradeLegs, Is.False);
                Assert.That(editor.IsDirty, Is.True);
                var sprite = pair.Client.EntMan.GetComponent<SpriteComponent>(editor.PreviewDummy);
                var leg = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(HumanoidVisualLayers.LLeg)];
                Assert.That(leg.ActualState!.RSI.Path.ToString(), Does.Contain("plantigrade.rsi"));
                Assert.That(editor.HandleTguiProfileAction("skin-color", "{\"value\":\"#985629\"}"), Is.True);
                Assert.That(editor.Profile.Appearance.PlantigradeLegs, Is.True);
                Assert.That(editor.HandleTguiProfileAction("plantigrade-legs", "{\"value\":false}"), Is.True);
                leg = (SpriteComponent.Layer) sprite[sprite.LayerMapGet(HumanoidVisualLayers.LLeg)];
                Assert.That(leg.ActualState!.RSI.Path.ToString(), Does.Not.Contain("plantigrade.rsi"));
                Assert.That(editor.HandleTguiProfileAction("plantigrade-legs", "{\"value\":\"invalid\"}"), Is.False);
            }
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies("Human"), editor.CharacterSlot);
            Assert.That(editor.HandleTguiProfileAction("plantigrade-legs", "{\"value\":true}"), Is.False);
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DraftUpdatesPreviewAndLayersRespectClothingWithoutAccumulating()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            var lobby = ui.GetUIController<LobbyUIController>();
            lobby.OpenCharacterEditor();
            var editor = (HumanoidProfileEditor) typeof(LobbyUIController).GetField("_profileEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lobby)!;
            var original = editor.Profile!.Appearance.BodyFeatures;
            bool Change(string part, string field, object value) => editor.HandleTguiProfileAction("body-feature", JsonSerializer.Serialize(new { part, field, value }));
            foreach (var part in BodyCustomizationCatalog.Order.Where(p => p != "womb")) Assert.That(Change(part, "enabled", "true"), Is.True);
            Assert.That(Change("vagina", "womb", "true"), Is.True);
            Assert.That(Change("womb", "enabled", "true"), Is.False);
            Assert.That(original, Is.Empty, "The saved appearance must remain independent of the draft");
            Assert.That(Change("breasts", "size", "g"), Is.True);
            Assert.That(Change("breasts", "secondaryEnabled", "true"), Is.True);
            Assert.That(Change("breasts", "secondaryColor", "#FF3300"), Is.True);
            Assert.That(Change("belly", "size", "4"), Is.False);
            Assert.That(Change("belly", "size", "3"), Is.True);
            Assert.That(Change("butt", "size", "5"), Is.False);
            Assert.That(Change("breasts", "size", "k"), Is.False);
            Assert.That(Change("penis", "size", "4"), Is.False);
            Assert.That(Change("anus", "size", "2"), Is.False);
            Assert.That(Change("butt", "anus", "true"), Is.True);
            Assert.That(editor.Profile.Appearance.BodyFeatures.Single(p => p.Part == "anus").Size, Is.EqualTo("1"));
            var preview = (Robust.Client.UserInterface.Controls.SpriteView) typeof(HumanoidProfileEditor)
                .GetProperty("SpriteView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(editor)!;
            Assert.That(preview.Stretch, Is.EqualTo(Robust.Client.UserInterface.Controls.SpriteView.StretchMode.None));
            Assert.That(Change("belly", "scale", 1.25f), Is.False);
            Assert.That(Change("anus", "shape", "squished"), Is.False);
            Assert.That(Change("breasts", "scale", "NaN"), Is.False);
            Assert.That(Change("penis", "shape", "../../bad"), Is.False);
            Assert.That(editor.IsDirty, Is.True);
            using var data = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            var parts = data.RootElement.GetProperty("bodyFeatures").EnumerateArray().ToArray();
            Assert.That(parts, Has.Length.EqualTo(7));
            Assert.That(parts.Any(p => p.GetProperty("part").GetString() == "womb"), Is.False);
            Assert.That(parts.Single(p => p.GetProperty("part").GetString() == "vagina").GetProperty("womb").GetBoolean(), Is.True);
            Assert.That(parts.Any(p => p.TryGetProperty("scale", out _)), Is.False);
            Assert.That(editor.Profile.Appearance.BodyFeatures.Single(p => p.Part == "womb").Enabled, Is.True);
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var sprite = entities.GetComponent<SpriteComponent>(editor.PreviewDummy);
            Assert.That(sprite.LayerMapTryGet("dl-body/breasts/FRONT", out var breast), Is.True);
            Assert.That(sprite[breast].RsiState.ToString(), Is.EqualTo("breasts_pair_g_0_FRONT"));
            Assert.That(sprite[breast].ActualRsi, Is.Not.Null);
            Assert.That(sprite.LayerMapTryGet("dl-body/breasts/SECONDARY", out var secondary), Is.True);
            Assert.That(sprite[secondary].RsiState.ToString(), Is.EqualTo("nipples_pair_g_0_FRONT"));
            Assert.That(sprite[secondary].Color, Is.EqualTo(Robust.Shared.Maths.Color.FromHex("#FF3300")));
            Assert.That(sprite.LayerMapTryGet("jumpsuit", out var uniform), Is.True);
            Assert.That(breast, Is.LessThan(uniform));
            Assert.That(sprite.LayerMapTryGet("dl-body/womb/FRONT", out _), Is.False);
            Assert.That(Change("breasts", "visibility", 2), Is.True);
            sprite.LayerMapTryGet("dl-body/breasts/FRONT", out breast);
            sprite.LayerMapTryGet("outerClothing", out var suit);
            Assert.That(breast, Is.GreaterThan(suit));
            sprite.LayerMapTryGet("dl-body/belly/FRONT", out var belly);
            sprite.LayerMapTryGet("jumpsuit", out uniform);
            Assert.That(belly, Is.LessThan(uniform));
            var count = sprite.AllLayers.Count();
            entities.System<HumanoidAppearanceSystem>().LoadProfile(editor.PreviewDummy, editor.Profile);
            Assert.That(sprite.AllLayers.Count(), Is.EqualTo(count));
            Assert.That(Change("breasts", "enabled", "false"), Is.True);
            Assert.That(sprite.LayerMapTryGet("dl-body/breasts/FRONT", out _), Is.False);
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().Close();
        });
        await pair.CleanReturnAsync();
    }
}
