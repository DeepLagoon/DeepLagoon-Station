using Content.Client._Mono.FireControl.UI;
using Content.Client._DeepLagoon.WebUI;
using Content.Client._DeepLagoon.Shuttles;
using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Server._Mono.FireControl;
using Content.Server.Shuttles.Systems;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared._Mono.FireControl;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class ShuttleCombatConsoleTests
{
    [Test]
    public async Task PilotAndStandaloneGunneryWindowsUseTheSameReusableWeaponPanel()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(WebUiCVars.InterfaceStyle, 1);
            var pilot = new ShuttleConsoleWindow();
            var gunner = new FireControlWindow();
            try
            {
                Assert.That(pilot.CombatPanel.Radar, Is.SameAs(pilot.CombatRadar));
                Assert.That(pilot.CombatRadar, Is.InstanceOf<Content.Client._DeepLagoon.Shuttles.CombatShuttleNavControl>());
                Assert.That(gunner.Radar, Is.Not.SameAs(pilot.CombatRadar));
                Assert.That(pilot.CombatPanel.WeaponsList, Is.Empty);
                Assert.That(gunner.WeaponsList, Is.Empty);
                Assert.That(pilot.Tgui, Is.Not.Null);
                var fireState = typeof(FireControlPanel).GetField("_currentState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                void CheckWeaponLamp(FireControlConsoleBoundInterfaceState? state, bool expected)
                {
                    fireState.SetValue(pilot.CombatPanel, state);
                    using var snapshot = System.Text.Json.JsonDocument.Parse(pilot.CombatPanel.TguiState().ToString());
                    Assert.That(snapshot.RootElement.GetProperty("hasWeapons").GetBoolean(), Is.EqualTo(expected));
                }
                CheckWeaponLamp(null, false);
                CheckWeaponLamp(new FireControlConsoleBoundInterfaceState(true, Array.Empty<FireControllableEntry>(), null!), false);
                CheckWeaponLamp(new FireControlConsoleBoundInterfaceState(true, new[] { default(FireControllableEntry) }, null!), true);
                CheckWeaponLamp(new FireControlConsoleBoundInterfaceState(false, new[] { default(FireControllableEntry) }, null!), false);
                CheckWeaponLamp(null, false);
                Assert.That(pilot.CombatRadar.Parent, Is.TypeOf<Robust.Client.UserInterface.Controls.LayoutContainer>(),
                    "Radar must be rendered natively above the browser viewport");
                Assert.That(pilot.CombatRadar.FitViewport, Is.True);
                Assert.That(gunner.Radar.FitViewport, Is.True);
                Assert.That(gunner.ConsoleHost, Is.Not.Null);
                var radarLayer = pilot.CombatRadar.Parent!;
                var glass = radarLayer.GetChild(radarLayer.ChildCount - 1);
                Assert.That(glass, Is.TypeOf<Content.Client._DeepLagoon.Shuttles.ShuttleRadarGlassControl>(),
                    "The aperture must cover native radar geometry and child labels in every mode");
                glass.Visible = true;
                glass.SetSize = new Vector2(400, 400);
                glass.Measure(new Vector2(400, 400));
                glass.Arrange(new Robust.Shared.Maths.UIBox2(0, 0, 400, 400));
                var hitTest = glass.GetType().GetMethod("HasPoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                Assert.That(hitTest.Invoke(glass, new object[] { new Vector2(200, 200) }), Is.False,
                    "Glass must pass center clicks through to the radar");
                Assert.That(hitTest.Invoke(glass, new object[] { new Vector2(10, 10) }), Is.True,
                    "Hidden radar corners must intercept clicks and cannot fire or select a dock");
                pilot.CombatRadar.SetSize = new Vector2(400, 300);
                pilot.CombatRadar.Measure(new Vector2(400, 300));
                pilot.CombatRadar.Arrange(new Robust.Shared.Maths.UIBox2(0, 0, 400, 300));
                var geometry = typeof(Content.Client.UserInterface.Controls.MapGridControl);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var center = (Vector2)geometry.GetProperty("MidPointVector", flags)!.GetValue(pilot.CombatRadar)!;
                Assert.That(center, Is.EqualTo((Vector2)pilot.CombatRadar.PixelSize / 2));
                var cursor = (Vector2)geometry.GetMethod("InverseMapPosition", flags)!.Invoke(pilot.CombatRadar, new object[] { center })!;
                Assert.That(cursor.Length(), Is.LessThan(0.001f), "A centered cursor must still target the radar origin after resizing");
                Assert.That(pilot.CombatPanel.FindControl<Robust.Client.UserInterface.Controls.PanelContainer>("RadarSettings").Visible, Is.False);
                Assert.That(pilot.CombatPanel.SelectBallisticButton.Parent!.Parent!.Parent!.Visible, Is.True,
                    "Weapon categories must remain available when duplicate radar settings are hidden");
                Assert.That(pair.Client.ResolveDependency<IConfigurationManager>().GetCVar(WebUiCVars.VanillaChat), Is.True);
                var nav = pilot.FindControl<NavScreen>("NavContainer");
                Assert.That(TguiActionData.TryParse("{\"id\":\"range\",\"value\":\"20000\"}", out var range), Is.True);
                nav.TguiAction(range!);
                Assert.That(pilot.CombatRadar.MaximumIFFDistance, Is.EqualTo(20000), "The original IFF range remains available");
                string? pressedPort = null;
                pilot.OnNetworkPortButtonPressed += (source, _) => pressedPort = source;
                TguiActionData.TryParse("{\"id\":\"port\",\"value\":\"8\"}", out var port);
                nav.TguiAction(port!);
                Assert.That(pressedPort, Is.EqualTo("device-button-8"));
                float? speed = 5;
                pilot.OnMaxShuttleSpeedChanged += value => speed = value;
                TguiActionData.TryParse("{\"id\":\"speed\",\"value\":\"\"}", out var unlimited);
                nav.TguiAction(unlimited!);
                Assert.That(speed, Is.Null, "An empty speed limit must retain the native unlimited behavior");
                typeof(ShuttleNavControl).GetField("_isMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(pilot.CombatRadar, true);
                pilot.SwitchMode(ShuttleConsoleWindow.ShuttleConsoleMode.Map);
                Assert.That(pilot.CombatRadar.IsMouseDown(), Is.False, "Switching away from the radar must stop a held burst");
            }
            finally
            {
                pilot.Dispose();
                gunner.Dispose();
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShipRadarHostsPreserveNativeControlsOnFallbackAndValidateBounds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            using var gunner = new FireControlWindow();
            using var scanner = new RadarConsoleWindow();
            var radar = scanner.FindControl<ShuttleNavControl>("RadarScreen");
            Assert.That(scanner.ConsoleHost, Is.Not.Null);
            Assert.That(scanner.ConsoleHost!.Kind, Is.EqualTo("mass"));
            scanner.SetHandheld(true);
            Assert.That(scanner.ConsoleHost.Kind, Is.EqualTo("handheld"));
            Assert.That(scanner.ConsoleHost.Tgui.ConsoleKeyboardFeedback, Is.False);
            Assert.That(scanner.ConsoleHost.GetChild(1).GetChild(1), Is.TypeOf<ShuttleRadarGlassControl>());
            Assert.That(((ShuttleRadarGlassControl)scanner.ConsoleHost.GetChild(1).GetChild(1)).ShowHardware, Is.False);
            Assert.That(scanner.FindControl<Robust.Client.UserInterface.Controls.Button>("IFFToggle").Visible, Is.False);
            Assert.That(scanner.MinSize.X, Is.LessThan(400));
            scanner.SetHandheld(false);
            Assert.That(scanner.ConsoleHost.Kind, Is.EqualTo("mass"));
            Assert.That(scanner.ConsoleHost.Tgui.ConsoleKeyboardFeedback, Is.True);
            Assert.That(radar.FitViewport, Is.True);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Action<string, string> Actions(TguiPanel panel) => (Action<string, string>)typeof(TguiPanel).GetField("OnAction", flags)!.GetValue(panel)!;
            var gunActions = Actions(gunner.ConsoleHost!.Tgui);
            var scannerActions = Actions(scanner.ConsoleHost!.Tgui);
            var previousIff = radar.ShowIFF;
            scannerActions("radar", "{\"id\":\"iff\"}");
            Assert.That(radar.ShowIFF, Is.EqualTo(!previousIff), "The web toggle must control the original scanner");
            var refreshed = false;
            gunner.OnServerRefresh += () => refreshed = true;
            gunActions("weapon", "{\"id\":\"refresh\"}");
            Assert.That(refreshed, Is.True, "Refresh must use the original bound-interface event");
            var selected = false;
            gunner.OnWeaponSelectionChanged += () => selected = true;
            gunActions("weapon", "{\"id\":\"all\"}");
            Assert.That(selected, Is.True);
            foreach (var invalid in new[] { "{\"x\":-1,\"y\":0,\"w\":0.5,\"h\":0.5}", "{\"x\":0,\"y\":0,\"w\":0,\"h\":0.5}", "{\"x\":0.8,\"y\":0,\"w\":0.5,\"h\":0.5}" })
            {
                Assert.That(TguiActionData.TryParse(invalid, out var args), Is.True);
                Assert.That(ShipRadarConsoleHost.TryViewport(args!, out _), Is.False);
            }
            Assert.That(TguiActionData.TryParse("{\"x\":0.1,\"y\":0.2,\"w\":0.5,\"h\":0.6}", out var valid), Is.True);
            Assert.That(ShipRadarConsoleHost.TryViewport(valid!, out _), Is.True);
            scanner.ConsoleHost.Tgui.SwitchToNative();
            gunner.ConsoleHost.Tgui.SwitchToNative();
            Assert.That(radar.FitViewport, Is.False);
            Assert.That(gunner.Radar.FitViewport, Is.False);
            Assert.That(scanner.ConsoleHost.Visible, Is.False);
            Assert.That(gunner.ConsoleHost.Visible, Is.False);
            Assert.That(radar.Parent, Is.Not.Null);
            Assert.That(gunner.Radar.Parent, Is.Not.Null);
            Assert.That(radar.ShowIFF, Is.EqualTo(!previousIff), "Fallback must retain the current IFF choice");
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 2);
            using var classicGunner = new FireControlWindow();
            using var classicScanner = new RadarConsoleWindow();
            Assert.That(classicGunner.ConsoleHost, Is.Null);
            Assert.That(classicScanner.ConsoleHost, Is.Null);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PilotStateIncludesDisconnectedAndConnectedWeaponServers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            entities.System<SharedMapSystem>().CreateMap(out var mapId);
            var grid = pair.Server.EntMan.System<SharedMapSystem>().CreateGridEntity(mapId);
            var console = entities.SpawnEntity("ComputerShuttle", new EntityCoordinates(grid.Owner, Vector2.Zero));
            var shuttles = entities.System<ShuttleConsoleSystem>();
            shuttles.RefreshState(console);
            var ui = entities.GetComponent<UserInterfaceComponent>(console);
            var state = (ShuttleBoundUserInterfaceState)ui.States[ShuttleConsoleUiKey.Key];
            Assert.That(state.FireControlState, Is.Not.Null);
            Assert.That(state.FireControlState!.Connected, Is.False);
            Assert.That(state.FireControlState.NavState, Is.SameAs(state.NavState));

            var server = entities.SpawnEntity("GunneryServerLow", new EntityCoordinates(grid.Owner, Vector2.One));
            var serverComponent = entities.GetComponent<FireControlServerComponent>(server);
            serverComponent.ConnectedGrid = grid.Owner;
            serverComponent.Consoles.Add(console);
            entities.GetComponent<FireControlConsoleComponent>(console).ConnectedServer = server;
            shuttles.RefreshState(console);
            state = (ShuttleBoundUserInterfaceState)ui.States[ShuttleConsoleUiKey.Key];
            Assert.That(state.FireControlState!.Connected, Is.True);
            Assert.That(state.FireControlState.NavState, Is.SameAs(state.NavState));
            Assert.That(entities.System<FireControlSystem>().GetConsoleState(console), Is.Not.Null);
            serverComponent.Consoles.Remove(console);
            shuttles.RefreshState(console);
            state = (ShuttleBoundUserInterfaceState)ui.States[ShuttleConsoleUiKey.Key];
            Assert.That(state.FireControlState, Is.Not.Null, "A lost connection must leave the refresh panel available");
            Assert.That(state.FireControlState!.Connected, Is.False);
        });
        await pair.CleanReturnAsync();
    }
}
