using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Voyages;
using Content.Shared._DeepLagoon.Voyages;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Input;
using Robust.Shared.Map;
namespace Content.IntegrationTests.Tests._DeepLagoon;
[TestFixture, NonParallelizable]
public sealed class SectorExpeditionWindowTests
{
    private static IEnumerable<Control> All(Control root)
    { foreach (var child in root.Children) { yield return child; foreach (var nested in All(child)) yield return nested; } }
    [Test]
    public async System.Threading.Tasks.Task NativeSectorWindowFitsBothSizesAndDistanceRefreshDoesNotReplaceLandingButtons()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        SectorExpeditionWindow window = null!; SectorExpeditionSnapshot state = null!;
        await pair.Client.WaitAssertion(() =>
        {
            window = new SectorExpeditionWindow(new NetEntity(42)); window.OpenCentered();
            state = new SectorExpeditionSnapshot { Console = new NetEntity(42), CurrentSector = "echo", Home = false,
                Selected = "echo", DepartureBlockReason = "БСС-двигатель не получает питание",
                Orders = [new(Guid.NewGuid(), "echo", "Исследовательский фонд", 30000, 30000, false, 0, 3)],
                Map = new VoyageSnapshot { Origin = "Колосс-Централ", Clock = 10, Bank = 75000 },
                Planets = [new(0, "Тестовая планета", "PlanetTypeHabitable", 400, 0, 2, false, false, 100, true)],
                Crews = [new(Guid.NewGuid(), "Капитан", "Разведчик", "Разведка и сопровождение", 3, 4, 15000, 2, 80, true, true)] };
            window.Apply(state);
            foreach (var width in new[] { 1020, 1240 })
            {
                var size = new Vector2(width, 790); window.SetSize = size; window.Measure(size); window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                foreach (var label in All(window).OfType<Label>().Where(l => l.VisibleInTree && l.Width > 0))
                    Assert.That(label.GlobalPosition.X + label.Width, Is.LessThanOrEqualTo(window.GlobalPosition.X + window.Width + 1));
            }
            var land = All(window).OfType<Button>().Single(b => b.Text == "Посадка" && b.Name != "PlanetLandingPopup");
            state.Planets = [state.Planets[0] with { Distance = 90, CanLand = false }]; window.Apply(state);
            Assert.That(All(window).OfType<Button>().Single(b => b.Text == "Посадка" && b.Name != "PlanetLandingPopup"), Is.SameAs(land)); Assert.That(land.Disabled, Is.True);
        });
        await pair.Client.WaitAssertion(() =>
        {
            All(window).OfType<TabContainer>().Single().CurrentTab = 1;
            var size = new Vector2(1240, 790); window.Measure(size); window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
        });
        var planetMap = All(window).OfType<VoyageMapControl>().Single(m => !m.OverviewDots);
        await pair.Client.WaitAssertion(() =>
        {
            var size = new Vector2(800, 600); planetMap.Measure(size); planetMap.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
            Assert.That(planetMap.Hit(new Vector2(5, 5)), Is.Null, "The test must click actual empty space in an arranged viewport.");
        });
        async System.Threading.Tasks.Task MapClick(VoyageMapControl control, Vector2 point)
        {
            var screenPoint = new ScreenCoordinates(control.GlobalPixelPosition + point, control.Window!.Id);
            await pair.Client.DoGuiEvent(control, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, BoundKeyState.Down, screenPoint, false, point / control.UIScale, point));
        }
        await MapClick(planetMap, planetMap.ScreenPosition(new Vector2(400, 0)));
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(planetMap.Selected, Is.EqualTo("planet-0"));
            foreach (var name in new[] { "Type", "Difficulty", "Objective", "Hostiles", "Duration", "Biome", "Modifiers", "Rewards" })
                Assert.That(All(window).Any(c => c.Name == "SectorMission" + name), Is.True, name);
            const string reason = "Дождитесь окончания БСС и перезарядки двигателя";
            state.Planets = [state.Planets[0] with { Completed = true, Distance = 25, CanLand = false, LandingBlockReason = reason }];
            window.Apply(state);
            Assert.That(All(window).Single(c => c.Name == "SectorPlanetBlockReason-0").Visible, Is.True);
            Assert.That(All(window).OfType<Button>().Single(b => b.Text == "Посадка" && b.Name != "PlanetLandingPopup").ToolTip, Is.EqualTo(reason));
            Assert.That(planetMap.LandingBlockReason, Is.EqualTo(reason));
            Assert.That(All(window).Any(c => c.Name == "SectorPlanetResearched-0"), Is.True);
            Assert.That(All(planetMap).OfType<Label>().Any(l => l.Text == "[Исследовано]"), Is.True);
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(All(planetMap).Single(c => c.Name == "PlanetResearched-planet-0").Visible, Is.True);
            state.Planets = [state.Planets[0] with { Completed = true, CanLand = true, LandingBlockReason = "" }];
            window.Apply(state);
            Assert.That(planetMap.LandingEnabled, Is.True, "An active research trip can revisit its completed planet.");
            state.Planets = [state.Planets[0] with { Completed = false, CanLand = true, LandingBlockReason = "" }];
            window.Apply(state);
            Assert.That(All(window).Single(c => c.Name == "SectorPlanetBlockReason-0").Visible, Is.False);
            Assert.That(planetMap.LandingEnabled, Is.True);
            Assert.That(All(window).Any(c => c.Name == "SectorPlanetResearched-0"), Is.False);
        });
        await pair.Client.WaitRunTicks(2);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(All(planetMap).Single(c => c.Name == "PlanetResearched-planet-0").Visible, Is.False);
        });
        await MapClick(planetMap, new Vector2(5, 5));
        await pair.Client.WaitAssertion(() => Assert.That(planetMap.Selected, Is.Empty));
        await MapClick(planetMap, planetMap.ScreenPosition(new Vector2(400, 0)));
        var pixel = window.Map.ScreenPosition(window.Model.System("echo").Position);
        var screen = new ScreenCoordinates(window.Map.GlobalPixelPosition + pixel, window.Map.Window!.Id);
        await pair.Client.DoGuiEvent(window.Map, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, BoundKeyState.Down, screen, false, pixel / window.Map.UIScale, pixel));
        Button choose = null!;
        await pair.Client.WaitAssertion(() =>
        {
            choose = All(window).OfType<Button>().Single(b => b.Text == "Взять заказ и вылететь");
            Assert.That(choose.Disabled, Is.True);
            Assert.That(choose.ToolTip, Is.EqualTo(state.DepartureBlockReason));
            Assert.That(All(window).OfType<Button>().Any(b => b.Text == "Отправиться по заказу"), Is.False);
            state.WithoutFtlDrive = true; window.Apply(state);
            Assert.That(All(window).OfType<Label>().Single(l => l.Text == "БСС: —").Visible, Is.False);
            state.CanDepart = true; state.DepartureBlockReason = ""; state.FlightSeconds = 35; window.Apply(state);
            choose = All(window).OfType<Button>().Single(b => b.Text == "Взять заказ и вылететь");
            Assert.That(choose.Disabled, Is.False);
            Assert.That(All(window).Single(c => c.Name == "SectorDepartureBlockReason").Visible, Is.True);
            var hidden = window.Model.Systems.First(s => !window.Map.IsSystemVisible(s.Id));
            Assert.That(window.Map.Hit(window.Map.ScreenPosition(hidden.Position)), Is.Not.EqualTo(hidden.Id));
        });
        async System.Threading.Tasks.Task Click(Button button)
        {
            var point = new ScreenCoordinates(button.GlobalPixelPosition + button.PixelSize / 2, button.Window!.Id);
            foreach (var key in new[] { BoundKeyState.Down, BoundKeyState.Up })
                await pair.Client.DoGuiEvent(button, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, key, point, false, Vector2.Zero, Vector2.Zero));
        }
        SectorExpeditionRequest sent = null!;
        await pair.Client.WaitAssertion(() => window.Request += request => sent = request);
        await Click(choose);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(sent.Action, Is.EqualTo(SectorExpeditionAction.AcceptResearch));
            Assert.That(sent.Order, Is.EqualTo(state.Orders[0].Id)); Assert.That(sent.Sector, Is.EqualTo("echo"));
            state.ActiveOrder = sent.Order; state.ClaimedSector = "echo"; state.InFlight = true; state.CanDepart = false; window.Apply(state);
            Assert.That(All(window).OfType<Button>().Single(b => b.Text == "Рейс выполняется").Disabled, Is.True);
            state.InFlight = false; state.Map.PlayerSector = "echo"; state.Map.Known = ["echo"]; window.Apply(state);
            Assert.That(window.Map.PlayerWorldPosition(), Is.EqualTo(window.Model.System("echo").Position));
        });
        Button center = null!, takeOff = null!;
        await pair.Client.WaitAssertion(() => center = All(window).OfType<Button>().Single(b => b.Text == "Центр"));
        await Click(center);
        await pair.Client.WaitAssertion(() => Assert.That(window.Map.Offset, Is.EqualTo(window.Model.System("echo").Position)));
        await pair.Client.WaitAssertion(() =>
        {
            var flight = Guid.NewGuid(); state.Map.PlayerFlight = flight; state.Map.Clock = 50;
            state.Map.Flights = [new(flight, true, Guid.NewGuid(), "Игрок", ["colossus", "helios", "echo"], 10, 80, 81, false, false, false, "Игрок", Kind: 4)];
            window.Apply(state);
        });
        await Click(center);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Vector2.Distance(window.Map.Offset, window.Map.PlayerWorldPosition()!.Value), Is.LessThan(15));
            Assert.That(window.Map.Offset, Is.Not.EqualTo(window.Model.System("echo").Position));
            state.OnPlanet = true; state.ActivePlanet = 0; window.Apply(state);
            takeOff = All(window).OfType<Button>().Single(b => b.Text == "Взлететь с планеты");
            Assert.That(takeOff.Disabled, Is.False);
            Assert.That(All(window).Any(c => c.Name == "SectorMissionLiveStatus"), Is.True);
        });
        await Click(takeOff);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(sent.Action, Is.EqualTo(SectorExpeditionAction.LeavePlanet));
            window.Close(); window.Dispose();
        });
        await pair.CleanReturnAsync();
    }
}
