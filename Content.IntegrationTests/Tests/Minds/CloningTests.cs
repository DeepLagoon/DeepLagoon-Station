using System.Linq;
using System.Numerics;
using Content.Client.Cloning.UI;
using Content.Server.Cloning;
using Content.Server.Cloning.Components;
using Content.Server.DeviceLinking.Systems;
using Content.Server.Ghost;
using Content.Server.Materials;
using Content.Server.Medical;
using Content.Server.Medical.Components;
using Content.Server.Power.Components;
using Content.Shared.Cloning;
using Content.Shared.Cloning.CloningConsole;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Minds;

[TestFixture]
public sealed class CloningTests
{
    [TestCase(true, TestName = "CloningConfirmationBeforeEjection")]
    [TestCase(false, TestName = "CloningConfirmationAfterEjection")]
    public async Task ConfirmationTransfersMind(bool acceptBeforeEjection)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        var map = await pair.CreateTestMap();
        var entities = pair.Server.EntMan;
        var session = pair.Player!;
        EntityUid original = default, clone = default, pod = default;
        Entity<MindComponent> mind = default;

        await pair.Server.WaitAssertion(() =>
        {
            var maps = entities.System<SharedMapSystem>();
            var consoleCoords = map.GridCoords.Offset(Vector2.UnitX);
            var podCoords = map.GridCoords.Offset(Vector2.UnitY);
            var originalCoords = map.GridCoords.Offset(-Vector2.UnitY);
            maps.SetTile(map.Grid, map.Grid.Comp, consoleCoords, map.Tile.Tile);
            maps.SetTile(map.Grid, map.Grid.Comp, podCoords, map.Tile.Tile);
            maps.SetTile(map.Grid, map.Grid.Comp, originalCoords, map.Tile.Tile);

            var scanner = entities.SpawnEntity("MedicalScanner", map.GridCoords);
            var console = entities.SpawnEntity("ComputerCloningConsole", consoleCoords);
            pod = entities.SpawnEntity("CloningPod", podCoords);
            var links = entities.System<DeviceLinkSystem>();
            Assert.That(links.ToggleLink(null, console, scanner, CloningConsoleComponent.ScannerPort, MedicalScannerComponent.ScannerPort), Is.True);
            Assert.That(links.ToggleLink(null, console, pod, CloningConsoleComponent.PodPort, CloningPodComponent.PodPort), Is.True);
            entities.GetComponent<ApcPowerReceiverComponent>(console).Powered = true;
            Assert.That(entities.System<MaterialStorageSystem>().TryChangeMaterialAmount(pod, "Biomass", 250), Is.True);

            original = entities.SpawnEntity("MobHuman", originalCoords);
            var minds = entities.System<SharedMindSystem>();
            mind = minds.CreateMind(session.UserId);
            minds.TransferTo(mind, original, mind: mind.Comp);
            entities.System<MobStateSystem>().ChangeMobState(original, MobState.Dead);
            if (mind.Comp.VisitingEntity == null)
                Assert.That(entities.System<GhostSystem>().OnGhostAttempt(mind, true, mind: mind.Comp), Is.True);

            Assert.That(mind.Comp.VisitingEntity, Is.Not.Null);
            Assert.That(session.AttachedEntity, Is.EqualTo(mind.Comp.VisitingEntity));

            entities.System<MedicalScannerSystem>().InsertBody(scanner, original, null);
            entities.EventBus.RaiseLocalEvent(console, new UiButtonPressedMessage(UiButton.Clone), true);
            var podComponent = entities.GetComponent<CloningPodComponent>(pod);
            Assert.That(podComponent.BodyContainer.ContainedEntity, Is.Not.Null);
            clone = podComponent.BodyContainer.ContainedEntity!.Value;
            Assert.That(podComponent.Status, Is.EqualTo(CloningPodStatus.NoMind));
        });

        await pair.RunTicksSync(5);

        if (!acceptBeforeEjection)
            await pair.Server.WaitAssertion(() => CompleteCycle(entities, pod));

        Button acceptButton = null!;
        Vector2 relativePixels = default, relativePosition = default;
        ScreenCoordinates clickPosition = default;
        await pair.Client.WaitAssertion(() =>
        {
            var ui = pair.Client.ResolveDependency<IUserInterfaceManager>();
            acceptButton = ui.WindowRoot.Children.OfType<AcceptCloningWindow>().Single().AcceptButton;
            relativePixels = acceptButton.PixelSize / 2;
            relativePosition = relativePixels / acceptButton.UIScale;
            clickPosition = new ScreenCoordinates(acceptButton.GlobalPixelPosition + relativePixels, acceptButton.Window!.Id);
        });
        await pair.Client.DoGuiEvent(acceptButton, new GUIBoundKeyEventArgs(
            EngineKeyFunctions.UIClick, BoundKeyState.Down, clickPosition, true, relativePosition, relativePixels));
        await pair.Client.DoGuiEvent(acceptButton, new GUIBoundKeyEventArgs(
            EngineKeyFunctions.UIClick, BoundKeyState.Up, clickPosition, true, relativePosition, relativePixels));

        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            if (acceptBeforeEjection)
            {
                Assert.That(entities.GetComponent<CloningPodComponent>(pod).Status, Is.EqualTo(CloningPodStatus.Cloning));
                CompleteCycle(entities, pod);
            }

            Assert.That(mind.Comp.VisitingEntity, Is.Null);
            Assert.That(mind.Comp.OwnedEntity, Is.EqualTo(clone));
            Assert.That(session.AttachedEntity, Is.EqualTo(clone));
            Assert.That(entities.GetComponent<MindContainerComponent>(clone).Mind, Is.EqualTo(mind.Owner));
            Assert.That(entities.GetComponent<MindContainerComponent>(original).Mind, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    private static void CompleteCycle(IEntityManager entities, EntityUid pod)
    {
        var component = entities.GetComponent<CloningPodComponent>(pod);
        entities.GetComponent<ApcPowerReceiverComponent>(pod).Powered = true;
        entities.System<CloningSystem>().Update(component.CloningTime);
        Assert.That(component.BodyContainer.ContainedEntity, Is.Null);
        Assert.That(component.Status, Is.EqualTo(CloningPodStatus.Idle));
    }
}
