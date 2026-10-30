#nullable enable
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.PDA;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.CartridgeLoader;
using Content.Shared.PDA;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class PdaOrbitalTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Action<string, string> Actions(PdaMenu menu) =>
        (Action<string, string>)typeof(TguiPanel).GetField("OnAction", Private)!.GetValue(menu.OrbitalHost!.Panel)!;

    private static PdaUpdateState State(bool extras = false) => new([], null, false, extras, extras,
        new PdaIdInfoText { ActualOwnerName = "Владелец", IdOwner = extras ? "Сотрудник" : null,
            JobTitle = extras ? "Инженер" : null, StationAlertLevel = "green", StationAlertColor = Color.Green },
        1234, extras ? "Корабль" : null, "Станция", extras, extras, "ABCD");

    [Test]
    public async Task OrbitalActionsFollowAvailableProgramsAndDeviceState()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            using var menu = new PdaMenu();
            menu.InitializeOrbital();
            var action = Actions(menu);
            var commands = new List<string>();
            menu.OnDeviceAction += commands.Add;
            action("flashlight", "{}");
            Assert.That(commands, Is.Empty, "No device commands before the first PDA snapshot");
            menu.UpdateState(State());
            foreach (var name in new[] { "music", "uplink", "lockUplink" }) action(name, "{}");
            Assert.That(commands, Is.Empty);
            foreach (var name in new[] { "id", "pen", "pai" }) action(name, "{}");
            Assert.That(commands, Is.EqualTo(new[] { "id", "pen", "pai" }), "Empty slots still accept insertion from the active hand");
            commands.Clear();
            action("flashlight", "{}"); action("ringtone", "{}");
            Assert.That(commands, Is.EqualTo(new[] { "flashlight", "ringtone" }));
            commands.Clear();
            menu.UpdateState(State(true));
            foreach (var name in new[] { "id", "pen", "pai", "music", "uplink", "lockUplink" }) action(name, "{}");
            Assert.That(commands.Count, Is.EqualTo(6));

            var uid = new EntityUid(42);
            var cartridge = new CartridgeComponent { ProgramName = "notekeeper-program-name", InstallationStatus = InstallationStatus.Cartridge };
            menu.UpdateAvailablePrograms([(uid, cartridge)]);
            var opened = new List<EntityUid>(); var installed = 0; var uninstalled = 0;
            menu.OnProgramItemPressed += opened.Add;
            menu.OnInstallButtonPressed += _ => installed++;
            menu.OnUninstallButtonPressed += _ => uninstalled++;
            var payload = "{\"id\":\"" + uid + "\"}";
            action("program", "{\"id\":\"999999\"}");
            action("program", payload); action("install", payload); action("uninstall", payload);
            Assert.That(opened, Is.EqualTo(new[] { uid }));
            Assert.That(installed, Is.EqualTo(1)); Assert.That(uninstalled, Is.Zero);
            cartridge.InstallationStatus = InstallationStatus.Installed;
            cartridge.Readonly = true;
            action("uninstall", payload);
            Assert.That(uninstalled, Is.Zero, "Readonly cartridges cannot be removed from TGUI");
            cartridge.Readonly = false;
            action("uninstall", payload);
            Assert.That(uninstalled, Is.EqualTo(1));
            menu.UpdateAvailablePrograms([]);
            action("program", payload); action("install", payload);
            Assert.That(opened.Count, Is.EqualTo(1), "Removed cartridges reject stale web actions");

            using var snapshot = JsonDocument.Parse((string)typeof(TguiPanel).GetField("_data", Private)!.GetValue(menu.OrbitalHost!.Panel)!);
            Assert.That(snapshot.RootElement.GetProperty("idOwner").GetString(), Is.EqualTo("Сотрудник"));
            Assert.That(snapshot.RootElement.GetProperty("address").GetString(), Is.EqualTo("ABCD"));
            menu.OrbitalHost.Panel.SwitchToNative();
            commands.Clear(); action("flashlight", "{}");
            Assert.That(commands, Is.Empty, "Queued browser commands are ignored after native fallback");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CartridgeControlsSurviveViewsAndBothStyleDirections()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 2);
            using (var classic = new PdaMenu())
            {
                classic.InitializeOrbital();
                Assert.That(classic.OrbitalHost, Is.Null, "A classic-only session creates no hidden browser");
                Assert.That(classic.ViewContainer.ChildCount, Is.EqualTo(4));
            }
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            using var menu = new PdaMenu();
            menu.AccentHColor = "#F0A060";
            menu.InitializeOrbital();
            var host = menu.OrbitalHost!;
            var program = new LineEdit { Text = "Несохранённая запись" };
            menu.ProgramView.AddChild(program);
            menu.ToProgramView("Заметки");
            var action = Actions(menu);
            action("viewport", "{\"x\":0.1,\"y\":0.2,\"w\":0.8,\"h\":0.5}");
            Assert.That(menu.ProgramView.Visible, Is.True);
            var bounds = host.ProgramViewport;
            action("viewport", "{\"x\":-1,\"y\":0,\"w\":2,\"h\":1}");
            Assert.That(host.ProgramViewport, Is.EqualTo(bounds));
            action("view", "{\"view\":0}");
            Assert.That(menu.ProgramView.Visible, Is.False);
            action("viewport", "{\"x\":0,\"y\":0,\"w\":1,\"h\":1}");
            Assert.That(menu.ProgramView.Visible, Is.False, "An old aperture cannot cover the home screen");
            menu.ToProgramView("Заметки");
            for (var i = 0; i < 3; i++)
            {
                host.Panel.SwitchToNative();
                Assert.That(menu.ProgramView.Parent, Is.EqualTo(menu.ViewContainer));
                Assert.That(menu.ViewContainer.ChildCount, Is.EqualTo(4));
                Assert.That(menu.ProgramView.Visible, Is.True);
                Assert.That(menu.FindControl<PanelContainer>("AccentH").Visible, Is.True);
                Assert.That(program.Text, Is.EqualTo("Несохранённая запись"));
                Assert.That(host.Panel.SwitchToTgui(), Is.True);
                Assert.That(menu.ProgramView.Parent, Is.EqualTo(host.ProgramLayer));
                Assert.That(menu.ViewContainer.ChildCount, Is.EqualTo(3));
                Assert.That(program.Disposed, Is.False);
            }
            Assert.That(cfg.GetCVar(WebUiCVars.InterfaceStyle), Is.EqualTo(1));
            Assert.That(cfg.GetCVar(WebUiCVars.WindowStyles), Is.Empty, "Failure fallback does not change player preferences");
        });
        await pair.CleanReturnAsync();
    }
}
