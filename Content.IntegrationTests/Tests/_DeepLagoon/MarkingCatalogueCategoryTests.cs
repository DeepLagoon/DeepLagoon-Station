#nullable enable
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class MarkingCatalogueCategoryTests
{
    [Test]
    public async Task EarsAndAntennaeUseCatalogueCategoriesIndependentOfTheirLayers()
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
            using var document = JsonDocument.Parse(editor.CreateTguiProfileState("markings").ToString());
            var options = document.RootElement.GetProperty("markingOptions").EnumerateArray()
                .ToDictionary(item => item.GetProperty("id").GetString()!);
            var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();

            foreach (var (id, option) in options)
            {
                var prototype = prototypes.Index<MarkingPrototype>(id);
                if (prototype.FilterType == MarkingEditorType.Antennae)
                    Assert.That(option.GetProperty("categoryId").GetString(), Is.EqualTo("Antennae"), id);
                if (prototype is { FilterType: MarkingEditorType.Ears, MarkingCategory: MarkingCategories.HeadSide })
                    Assert.That(option.GetProperty("categoryId").GetString(), Is.EqualTo("HeadTop"), id);
            }

            foreach (var id in new[] { "BlueMoonEarsHumanPedeHeadSide", "PointyEarsNone", "TichelSimpleLizard",
                         "LizardFrillsAxolotl", "LizardFrillsHood", "SynthAccessoryHornquadrato" })
            {
                Assert.That(options[id].GetProperty("categoryId").GetString(), Is.EqualTo("HeadTop"), id);
                Assert.That(options[id].GetProperty("part").GetString(), Is.EqualTo("HeadSide"), id);
                Assert.That(prototypes.Index<MarkingPrototype>(id).MarkingCategory, Is.EqualTo(MarkingCategories.HeadSide), id);
            }

            foreach (var id in new[] { "BlueMoonEarsMam_earsThinantHeadSide", "SynthAccessoryAntennaSimple",
                         "RobotAntennaTv", "MothAntennasDefault", "ChitinidAntennasDefault" })
            {
                Assert.That(options[id].GetProperty("categoryId").GetString(), Is.EqualTo("Antennae"), id);
                Assert.That(options[id].GetProperty("typeId").GetString(), Is.EqualTo("Antennae"), id);
            }

            Assert.That(options["ScreenStatic"].GetProperty("categoryId").GetString(), Is.EqualTo("HeadSide"));
            ui.WindowRoot.Children.OfType<CharacterEditorWindow>().Single().CloseConfirmed();
        });
        await pair.CleanReturnAsync();
    }
}
