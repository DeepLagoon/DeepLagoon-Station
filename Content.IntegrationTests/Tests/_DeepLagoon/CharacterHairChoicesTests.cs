using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Client._Mono.Company;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client.Players.PlayTimeTracking;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class CharacterHairChoicesTests
{
    [TestCase("Human", true)]
    [TestCase("Diona", false)]
    public async Task EditorHairChoicesRespectSpecies(string species, bool hasStyles)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { InLobby = true });
        var client = pair.Client;
        await client.WaitAssertion(() =>
        {
            using var editor = new HumanoidProfileEditor(
                client.ResolveDependency<IClientPreferencesManager>(),
                client.ResolveDependency<IConfigurationManager>(),
                client.ResolveDependency<IEntityManager>(),
                client.ResolveDependency<IFileDialogManager>(),
                client.ResolveDependency<ILogManager>(),
                client.ResolveDependency<IPlayerManager>(),
                client.ResolveDependency<IPrototypeManager>(),
                client.ResolveDependency<IResourceCache>(),
                client.ResolveDependency<JobRequirementsManager>(),
                client.ResolveDependency<MarkingManager>(),
                client.ResolveDependency<CompanyManager>());
            editor.SetProfile(HumanoidCharacterProfile.DefaultWithSpecies(species), 0);
            using var state = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            foreach (var key in new[] { "hairOptions", "beardOptions" })
            {
                var choices = state.RootElement.GetProperty(key).EnumerateArray().ToArray();
                Assert.That(choices.Length, hasStyles ? Is.GreaterThan(4) : Is.EqualTo(1), key);
                Assert.That(choices.Skip(1).All(choice => choice.GetProperty("iconId").GetString() == "marking/" + choice.GetProperty("id").GetString()), Is.True, key);
                Assert.That(choices.All(choice => !choice.TryGetProperty("images", out _)), Is.True, "Catalogue must not resend PNGs on profile edits");
            }
            using var dynamic = JsonDocument.Parse(editor.CreateTguiProfileState("appearance", includeCatalogs: false).ToString());
            Assert.That(dynamic.RootElement.TryGetProperty("hairOptions", out _), Is.False);
            Assert.That(dynamic.RootElement.TryGetProperty("speciesOptions", out _), Is.False);
            Assert.That(dynamic.RootElement.GetProperty("name").GetString(), Is.EqualTo(editor.Profile!.Name));
            using var jobCatalog = JsonDocument.Parse(editor.CreateTguiProfileState("jobs").ToString());
            using var jobState = JsonDocument.Parse(editor.CreateTguiProfileState("jobs", includeCatalogs: false).ToString());
            Assert.That(jobState.RootElement.TryGetProperty("departments", out _), Is.False);
            foreach (var department in jobCatalog.RootElement.GetProperty("departments").EnumerateArray())
                foreach (var job in department.GetProperty("jobs").EnumerateArray())
                    Assert.That(jobState.RootElement.GetProperty("jobState").GetProperty(job.GetProperty("id").GetString()!).GetProperty("priority").GetInt32(), Is.EqualTo(job.GetProperty("priority").GetInt32()));
            using var traitCatalog = JsonDocument.Parse(editor.CreateTguiProfileState("traits").ToString());
            using var traitState = JsonDocument.Parse(editor.CreateTguiProfileState("traits", includeCatalogs: false).ToString());
            Assert.That(traitState.RootElement.TryGetProperty("traits", out _), Is.False);
            foreach (var trait in traitCatalog.RootElement.GetProperty("traits").EnumerateArray())
            {
                var live = traitState.RootElement.GetProperty("traitState").GetProperty(trait.GetProperty("id").GetString()!);
                Assert.That(live.GetProperty("visible").GetBoolean(), Is.EqualTo(trait.GetProperty("visible").GetBoolean()));
                Assert.That(live.GetProperty("selected").GetBoolean(), Is.EqualTo(trait.GetProperty("selected").GetBoolean()));
            }
            Assert.That(editor.HandleTguiProfileAction("name", "{\"value\":\"Optimized draft\"}"), Is.True);
            var panel = (TguiPanel)typeof(HumanoidProfileEditor).GetField("_profileWeb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var catalog = typeof(TguiPanel).GetField("_staticData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);
            Assert.That(editor.HandleTguiProfileAction("name", "{\"value\":\"Second draft\"}"), Is.True);
            Assert.That(typeof(TguiPanel).GetField("_staticData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel), Is.SameAs(catalog));
            var draft = editor.Profile;
            using var originalState = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
            var originalTabs = originalState.RootElement.GetProperty("tabs").GetArrayLength();
            panel.SwitchToNative();
            Assert.That(editor.Profile, Is.SameAs(draft), "Native recovery must preserve the current draft");
            Assert.That(((Control)typeof(HumanoidProfileEditor).GetField("_tguiHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!).Parent, Is.Null);
            var tabs = editor.Children.SelectMany(child => child.Children).OfType<Robust.Client.UserInterface.Controls.TabContainer>().FirstOrDefault();
            Assert.That(tabs, Is.Not.Null, "Native tabs must return to the editor column");
            Assert.That(tabs!.Visible, Is.True);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.That(panel.SwitchToTgui(), Is.True);
                Assert.That(editor.Profile, Is.SameAs(draft));
                using var restored = JsonDocument.Parse(editor.CreateTguiProfileState("appearance").ToString());
                Assert.That(restored.RootElement.GetProperty("tabs").GetArrayLength(), Is.EqualTo(originalTabs), "Native-only tabs must not duplicate TGUI navigation");
                panel.SwitchToNative();
                Assert.That(tabs.Visible, Is.True);
            }
        });
        await pair.CleanReturnAsync();
    }
}
