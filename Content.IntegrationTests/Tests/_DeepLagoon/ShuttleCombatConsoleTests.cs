#nullable enable
using Content.Client._Mono.FireControl.UI;
using Content.Client._DeepLagoon.WebUI;
using Content.Client._DeepLagoon.Shuttles;
using System.Numerics;
using System.Linq;
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
    public async Task RadarWeaponClicksToggleSelectionWithoutFiringAndGhostsCannotBeSelected()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var entities = pair.Client.EntMan;
            using var radar = new FireControlNavControl();
            using var panel = new FireControlPanel(radar);
            var first = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var second = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            var firstNet = entities.GetNetEntity(first);
            var secondNet = entities.GetNetEntity(second);
            var nav = new NavInterfaceState(512, null, null, new(), Content.Shared._NF.Shuttles.Events.InertiaDampeningMode.Dampen);
            var weapons = new[]
            {
                new FireControllableEntry(firstNet, default, "First"),
                new FireControllableEntry(secondNet, default, "Second"),
            };
            panel.UpdateStatus(new FireControlConsoleBoundInterfaceState(true, weapons, nav));
            radar.UpdateControllables(first, weapons);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var markers = (System.Collections.Generic.List<(NetEntity? Entity, Vector2 Position)>) typeof(FireControlNavControl)
                .GetField("_weaponMarkers", flags)!.GetValue(radar)!;
            // Hit boxes from the previous draw are in pixels, matching the native radar's input contract.
            var firstPosition = new Vector2(40, 50);
            var secondPosition = new Vector2(80, 50);
            var ghostPosition = new Vector2(120, 50);
            markers.Add((firstNet, firstPosition * radar.UIScale));
            markers.Add((secondNet, secondPosition * radar.UIScale));
            markers.Add((null, ghostPosition * radar.UIScale));
            var fireRequests = 0;
            radar.OnRadarClick += _ => fireRequests++;
            void Click(Vector2 position)
            {
                var down = new Robust.Client.UserInterface.GUIBoundKeyEventArgs(Robust.Shared.Input.EngineKeyFunctions.UIClick,
                    Robust.Shared.Input.BoundKeyState.Down, default, true, position, position * radar.UIScale);
                typeof(FireControlNavControl).GetMethod("KeyBindDown", flags)!.Invoke(radar, [down]);
                Assert.That(radar.IsMouseDown(), Is.False, "Selecting a marker must not begin a held burst");
                var up = new Robust.Client.UserInterface.GUIBoundKeyEventArgs(Robust.Shared.Input.EngineKeyFunctions.UIClick,
                    Robust.Shared.Input.BoundKeyState.Up, default, true, position, position * radar.UIScale);
                typeof(FireControlNavControl).GetMethod("KeyBindUp", flags)!.Invoke(radar, [up]);
            }
            Click(firstPosition);
            Assert.That(panel.WeaponsList[firstNet].Pressed, Is.True);
            Assert.That(panel.WeaponsList[secondNet].Pressed, Is.False);
            Click(secondPosition);
            Assert.That(panel.WeaponsList[firstNet].Pressed && panel.WeaponsList[secondNet].Pressed, Is.True);
            Click(firstPosition);
            Assert.That(panel.WeaponsList[firstNet].Pressed, Is.False);
            Click(ghostPosition);
            Assert.That(panel.WeaponsList[secondNet].Pressed, Is.True);
            Assert.That(fireRequests, Is.Zero, "Neither live markers nor destroyed ghosts may send a firing click");
            panel.UpdateStatus(new FireControlConsoleBoundInterfaceState(true, [weapons[0]], nav));
            radar.UpdateControllables(first, [weapons[0]]);
            Assert.That(panel.WeaponsList.ContainsKey(secondNet), Is.False, "A destroyed gun must lose its selection button");
            Click(secondPosition);
            Assert.That(fireRequests, Is.Zero, "A stale hit box must not select or fire a removed gun");
            Assert.That(radar.TrySelectWeapon(new Vector2(300, 300)), Is.False, "Free radar space remains available for aiming");
            entities.DeleteEntity(first);
            entities.DeleteEntity(second);
        });
        await pair.CleanReturnAsync();
    }
    [Test]
    public async Task ClassicNavigationFitsRadarAndScrollablePanelsWithManyWeapons()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            using var nav = new NavScreen { Visible = true };
            nav.FireControlPanel.Visible = true;
            var list = nav.FireControlPanel.FindControl<Robust.Client.UserInterface.Controls.BoxContainer>("ControllablesBox");
            for (var i = 0; i < 80; i++)
                list.AddChild(new Robust.Client.UserInterface.Controls.Button
                {
                    Text = $"Корабельное орудие {i + 1}", HorizontalExpand = true
                });
            foreach (var size in new[] { new Vector2(1050, 580), new Vector2(1150, 680), new Vector2(1550, 880) })
            {
                nav.Measure(size);
                nav.Arrange(new Robust.Shared.Maths.UIBox2(Vector2.Zero, size));
                Assert.That(nav.DesiredSize.X, Is.LessThanOrEqualTo(size.X + 1));
                Assert.That(nav.DesiredSize.Y, Is.LessThanOrEqualTo(size.Y + 1));
                Assert.That(nav.CombatRadar.FitViewport, Is.True);
                Assert.That(nav.CombatRadar.Size.X, Is.GreaterThan(250));
                Assert.That(nav.CombatRadar.Size.Y, Is.GreaterThan(250));
                var settings = nav.FindControl<Robust.Client.UserInterface.Controls.ScrollContainer>("NavigationScroll");
                foreach (var panel in new Robust.Client.UserInterface.Control[] { settings, nav.FireControlPanel })
                {
                    Assert.That(panel.GlobalPosition.X, Is.GreaterThanOrEqualTo(nav.CombatRadar.GlobalPosition.X + nav.CombatRadar.Size.X));
                    Assert.That(panel.GlobalPosition.X + panel.Size.X, Is.LessThanOrEqualTo(size.X + 1));
                    Assert.That(panel.Size.Y, Is.LessThanOrEqualTo(size.Y + 1));
                }
                Assert.That(list.Parent, Is.TypeOf<Robust.Client.UserInterface.Controls.ScrollContainer>());
                Assert.That(list.Size.Y, Is.GreaterThan(list.Parent!.Size.Y), "Weapon lists must scroll within their column");
            }
        });
        await pair.CleanReturnAsync();
    }

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
                Assert.That(radarLayer.ChildCount, Is.EqualTo(4));
                Assert.That(radarLayer.Children.All(child => child is Content.Client.UserInterface.Controls.MapGridControl), Is.True,
                    "The rectangular pilot radar must have no circular glass overlay");
                Assert.That(radarLayer.Children.OfType<ShuttleHullNavMapControl>().Count(), Is.EqualTo(1));
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
            Assert.That(radar.CircularRadar, Is.True);
            Assert.That(scanner.ConsoleHost.GetChild(1).GetChild(1), Is.TypeOf<ShuttleRadarGlassControl>());
            Assert.That(((ShuttleRadarGlassControl)scanner.ConsoleHost.GetChild(1).GetChild(1)).ShowHardware, Is.False);
            Assert.That(scanner.FindControl<Robust.Client.UserInterface.Controls.Button>("IFFToggle").Visible, Is.False);
            Assert.That(scanner.MinSize.X, Is.LessThan(400));
            scanner.SetHandheld(false);
            Assert.That(scanner.ConsoleHost.Kind, Is.EqualTo("mass"));
            Assert.That(scanner.ConsoleHost.Tgui.ConsoleKeyboardFeedback, Is.False);
            Assert.That(radar.CircularRadar, Is.False);
            Assert.That(gunner.Radar.CircularRadar, Is.False);
            Assert.That(radar.FitViewport, Is.True);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Action<string, string> Actions(TguiPanel panel) => (Action<string, string>)typeof(TguiPanel).GetField("OnAction", flags)!.GetValue(panel)!;
            var gunActions = Actions(gunner.ConsoleHost!.Tgui);
            var scannerActions = Actions(scanner.ConsoleHost!.Tgui);
            const string rectangularViewport = "{\"x\":0.1,\"y\":0.2,\"w\":0.7,\"h\":0.4}";
            scannerActions("viewport", rectangularViewport);
            gunActions("viewport", rectangularViewport);
            Assert.That(scanner.ConsoleHost.GetChild(1).GetChild(1).Visible, Is.False);
            Assert.That(gunner.ConsoleHost.GetChild(1).GetChild(1).Visible, Is.False);
            scanner.SetHandheld(true);
            Assert.That(radar.CircularRadar, Is.True);
            Assert.That(scanner.ConsoleHost.GetChild(1).GetChild(1).Visible, Is.True);
            scanner.SetHandheld(false);
            Assert.That(radar.CircularRadar, Is.False);
            Assert.That(scanner.ConsoleHost.GetChild(1).GetChild(1).Visible, Is.False);
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
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            using var classicGunner = new FireControlWindow();
            using var classicScanner = new RadarConsoleWindow();
            Assert.That(classicGunner.ConsoleHost!.Tgui.Web.Visible, Is.False);
            Assert.That(classicScanner.ConsoleHost!.Tgui.Web.Visible, Is.False);
            // Off-tree fixtures have no UI frame to run the deferred style selection.
            classicGunner.ConsoleHost.Tgui.SwitchToNative();
            classicScanner.ConsoleHost.Tgui.SwitchToNative();
            Assert.That(classicGunner.ConsoleHost!.Tgui.UsingNative, Is.True);
            Assert.That(classicScanner.ConsoleHost!.Tgui.UsingNative, Is.True);
            Assert.That(classicGunner.ConsoleHost.Visible, Is.False);
            Assert.That(classicScanner.ConsoleHost.Visible, Is.False);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PilotStatusHidesNativeViewportsAndRetainsFallback()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(WebUiCVars.InterfaceStyle, 1);
            using var pilot = new ShuttleConsoleWindow();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var actions = (Action<string, string>)typeof(TguiPanel).GetField("OnAction", flags)!.GetValue(pilot.Tgui)!;
            var views = (Robust.Client.UserInterface.Control[])typeof(ShuttleConsoleWindow).GetField("_views", flags)!.GetValue(pilot)!;
            actions("viewport", "{\"x\":0.1,\"y\":0.1,\"w\":0.7,\"h\":0.4}");
            Assert.That(pilot.CombatRadar.Visible, Is.True);
            var bounds = (Vector4)typeof(ShuttleConsoleWindow).GetField("_viewport", flags)!.GetValue(pilot)!;
            Assert.That(bounds.Z, Is.EqualTo(0.7f));
            Assert.That(bounds.W, Is.EqualTo(0.4f));
            typeof(ShuttleNavControl).GetField("_isMouseDown", flags)!.SetValue(pilot.CombatRadar, true);
            actions("mode", "{\"value\":3}");
            foreach (var view in views) Assert.That(view.Visible, Is.False);
            Assert.That(pilot.CombatRadar.IsMouseDown(), Is.False);
            pilot.UpdateHullState(new ShuttleHullState
            {
                Width = 2, Height = 1, Cells = new byte[] { 5, 11 },
                ExpectedThrusters = 1, ExpectedWeapons = 1, MissingWeapons = 1,
            });
            using (var state = System.Text.Json.JsonDocument.Parse(
                       typeof(ShuttleConsoleWindow).GetField("_hullState", flags)!.GetValue(pilot)!.ToString()!))
            {
                Assert.That(state.RootElement.GetProperty("present").GetBoolean(), Is.True);
                var hullMap = (Content.Client._DeepLagoon.Shuttles.ShuttleHullNavMapControl)
                    typeof(ShuttleConsoleWindow).GetField("_hullMap", flags)!.GetValue(pilot)!;
                Assert.That(hullMap.Hull!.Cells, Is.EqualTo(new byte[] { 5, 11 }),
                    "The native map owns cell rendering; TGUI carries summary counts.");
                Assert.That(state.RootElement.GetProperty("weapons").GetProperty("missing").GetInt32(), Is.EqualTo(1));
            }
            actions("mode", "{\"value\":0}");
            Assert.That(pilot.CombatRadar.Visible, Is.True);
            actions("mode", "{\"value\":3}");
            pilot.Tgui.SwitchToNative();
            Assert.That(pilot.CombatRadar.FitViewport, Is.True, "Classic pilot radar must fit beside both control columns");
            Assert.That(pilot.CombatRadar.GetValue<float>(Robust.Client.UserInterface.Controls.LayoutContainer.AnchorRightProperty), Is.EqualTo(1f));
            Assert.That(pilot.CombatRadar.GetValue<float>(Robust.Client.UserInterface.Controls.LayoutContainer.AnchorBottomProperty), Is.EqualTo(1f));
            Assert.That(pilot.CombatRadar.Visible, Is.True);
            Assert.That(pilot.Tgui.Visible, Is.False);
            Assert.That(pilot.CombatRadar.Parent, Is.Not.SameAs(views[1].Parent));
            pilot.UpdateHullState(null);
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
