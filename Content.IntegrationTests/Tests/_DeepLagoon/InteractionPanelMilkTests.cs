using System.Linq;
using System.Reflection;
using System.Text.Json;
using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.BodyCustomization;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class InteractionPanelMilkTests
{
    [TestCase("MobHuman", "Human")]
    [TestCase("MobVulpkanin", "Vulpkanin")]
    public async Task PanelFillsActiveContainerWithMilk(string prototype, string species)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default, vessel = default;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clientSystem = typeof(Content.Client._DeepLagoon.InteractionPanel.InteractionPanelSystem);
        var windowType = typeof(Content.Client._DeepLagoon.InteractionPanel.InteractionPanelWindow);

        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            body = entities.SpawnEntity(prototype, map.GridCoords);
            var feature = BodyCustomizationCatalog.Default("breasts");
            feature.Enabled = true;
            var profile = HumanoidCharacterProfile.DefaultWithSpecies(species)
                .WithInteractionPanelConsent(InteractionPanelCategory.Erotic, InteractionPanelConsent.Yes);
            profile.Appearance.BodyFeatures = [feature];
            entities.System<SharedHumanoidAppearanceSystem>().LoadProfile(body, profile);
            entities.GetComponent<InteractionPanelPreferencesComponent>(body).Profile = profile;
            entities.EnsureComponent<BlueMoonPhysiologyComponent>(body).LastEmission["breasts"] =
                pair.Server.ResolveDependency<IGameTiming>().CurTime - TimeSpan.FromSeconds(200);
            vessel = entities.SpawnEntity("Beaker", map.GridCoords);
            Assert.That(entities.System<SharedHandsSystem>().TryPickup(body, vessel, animate: false), Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
            Assert.That(entities.System<InteractionPanelArousalSystem>().CanExposePart(body, "breasts"), Is.True);
        });

        await pair.RunTicksSync(pair.SecondsToTicks(2));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.GetComponent<InteractionPanelArousalComponent>(body).ProducingParts, Does.Contain("breasts"));
            entities.System<InteractionPanelSystem>().OpenPanel(body, body);
        });
        await pair.RunTicksSync(15);
        await pair.Client.WaitAssertion(() =>
        {
            var window = clientSystem.GetField("_panel", flags)!.GetValue(
                pair.Client.EntMan.System<Content.Client._DeepLagoon.InteractionPanel.InteractionPanelSystem>());
            Assert.That(window, Is.Not.Null);
            var tgui = windowType.GetField("_panel", flags)!.GetValue(window)!;
            using var data = JsonDocument.Parse((string) tgui.GetType().GetField("_data", flags)!.GetValue(tgui)!);
            Assert.That(data.RootElement.GetProperty("producingParts").EnumerateArray()
                .Any(part => part.GetProperty("id").GetString() == "breasts"), Is.True,
                "The client panel offers breasts as a fluid source");
            windowType.GetMethod("OnAction", flags)!.Invoke(window,
                ["climax", "{\"part\":\"breasts\",\"destination\":\"surface\",\"container\":\"1\"}"]);
        });

        await pair.RunTicksSync(pair.SecondsToTicks(5));
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            Assert.That(entities.System<SharedHandsSystem>().GetActiveItem(body), Is.EqualTo(vessel));
            Assert.That(entities.System<SharedSolutionContainerSystem>()
                .TryGetRefillableSolution(vessel, out _, out var contents), Is.True);
            Assert.That(contents!.GetTotalPrototypeQuantity("DLPhysiologyMilk"), Is.EqualTo(FixedPoint2.New(50)),
                "The panel request completes its delay and transfers actual milk into the active container");
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            entities.DeleteEntity(map.MapUid);
        });
        await pair.ReallyBeIdle();
        await pair.CleanReturnAsync();
    }
}
