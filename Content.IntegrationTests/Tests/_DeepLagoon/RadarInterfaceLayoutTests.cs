using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Shuttles.UI;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class RadarInterfaceLayoutTests
{
    [Test]
    public async Task ClassicShuttleTabsKeepRadarsInsideTheirContentRow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            using var pilot = new ShuttleConsoleWindow();
            pilot.Tgui.SwitchToNative();
            pilot.OpenCentered();
            for (var cycle = 0; cycle < 2; cycle++)
            {
                foreach (var size in new[] { new Vector2(1100, 700), new Vector2(1200, 800), new Vector2(1400, 900) })
                {
                    foreach (var mode in new[] { ShuttleConsoleWindow.ShuttleConsoleMode.Nav,
                                 ShuttleConsoleWindow.ShuttleConsoleMode.Map, ShuttleConsoleWindow.ShuttleConsoleMode.Dock })
                    {
                        pilot.SwitchMode(mode);
                        foreach (var control in Descendants(pilot).Reverse()) control.InvalidateMeasure();
                        pilot.InvalidateMeasure();
                        pilot.Measure(size);
                        pilot.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));

                        var radar = Descendants(pilot).OfType<BaseShuttleControl>().Single(view => view.VisibleInTree);
                        var row = radar.Parent!;
                        var modeButtons = pilot.FindControl<BoxContainer>("ModeButtons");
                        var context = $"{mode}, {size}, cycle {cycle}";
                        Assert.That(radar.Size.X, Is.GreaterThan(100), context);
                        Assert.That(radar.Size.Y, Is.GreaterThan(100), context);
                        Assert.That(radar.Position.Y, Is.GreaterThanOrEqualTo(0), context);
                        Assert.That(radar.Position.Y + radar.Size.Y, Is.LessThanOrEqualTo(row.Size.Y), context);
                        Assert.That(radar.GlobalPosition.Y,
                            Is.GreaterThanOrEqualTo(modeButtons.GlobalPosition.Y + modeButtons.Size.Y), context);
                        foreach (var sibling in row.Children.Where(child => child != radar && child.Visible))
                            Assert.That(radar.Position.X + radar.Size.X, Is.LessThanOrEqualTo(sibling.Position.X), context);
                    }
                }
                Assert.That(pilot.Tgui.SwitchToTgui(), Is.True);
                pilot.Tgui.SwitchToNative();
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RepeatedRadarSwitchesKeepThePresentationAndCloseButtonVisible()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            foreach (var closeInNative in new[] { false, true })
            {
                var mass = new RadarConsoleWindow();
                var handheld = new RadarConsoleWindow();
                handheld.SetHandheld(true);
                var gunner = new Content.Client._Mono.FireControl.UI.FireControlWindow();
                var cases = new (FancyWindow Window, TguiPanel Panel)[]
                {
                    (mass, mass.ConsoleHost!.Tgui),
                    (handheld, handheld.ConsoleHost!.Tgui),
                    (gunner, gunner.ConsoleHost!.Tgui),
                };
                foreach (var (window, panel) in cases)
                {
                    window.OpenCentered();
                    var closeButton = window.FindControl<TextureButton>("CloseButton");
                    for (var cycle = 0; cycle < 12; cycle++)
                    {
                        panel.SwitchToNative();
                        Assert.That(closeButton.VisibleInTree, Is.True, window.GetType().Name);
                        Assert.That(Descendants(window).OfType<ShuttleNavControl>().Single().VisibleInTree, Is.True);
                        Assert.That(panel.SwitchToTgui(), Is.True);
                        Assert.That(panel.VisibleInTree, Is.True,
                            "TGUI must remain visible after switching: " + window.GetType().Name);
                        Assert.That(closeButton.VisibleInTree, Is.True,
                            "The shared window header must remain visible: " + window.GetType().Name);
                    }
                    if (closeInNative) panel.SwitchToNative();
                    var closed = false;
                    window.OnClose += () => closed = true;
                    window.Close();
                    Assert.That(closed, Is.True);
                    Assert.That(window.IsOpen, Is.False);
#pragma warning disable CS0618
                    window.Dispose();
#pragma warning restore CS0618
                }
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReturningToNativeRestoresVisibleRadarGeometryInEveryConsole()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            cfg.SetCVar(WebUiCVars.WindowStyles, "");
            var pilot = new ShuttleConsoleWindow();
            var gunner = new Content.Client._Mono.FireControl.UI.FireControlWindow();
            var mass = new RadarConsoleWindow();
            var handheld = new RadarConsoleWindow();
            handheld.SetHandheld(true);
            var pilotViews = Descendants(pilot).OfType<MapGridControl>()
                .Where(view => view.Parent == pilot.CombatRadar.Parent
                    && view is not Content.Client._DeepLagoon.Shuttles.ShuttleHullNavMapControl).ToArray();
            Assert.That(pilotViews.Length, Is.EqualTo(3));
            var cases = new (BaseWindow Window, TguiPanel Panel, MapGridControl[] Views)[]
            {
                (pilot, pilot.Tgui, pilotViews),
                (gunner, gunner.ConsoleHost!.Tgui, new MapGridControl[] { gunner.Radar }),
                (mass, mass.ConsoleHost!.Tgui, Descendants(mass).OfType<ShuttleNavControl>().ToArray()),
                (handheld, handheld.ConsoleHost!.Tgui, Descendants(handheld).OfType<ShuttleNavControl>().ToArray()),
            };
            foreach (var (window, panel, views) in cases)
            {
                panel.SwitchToNative();
                var sizes = views.Select(view => view.SetSize).ToArray();
                var fits = views.Select(view => view.FitViewport).ToArray();
                var parents = new Dictionary<Control, Control>();
                window.OpenCentered();
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    Assert.That(panel.SwitchToTgui(), Is.True);
                    // CEF viewport updates write size and attached layout offsets on each frame.
                    foreach (var view in views)
                    {
                        LayoutContainer.SetPosition(view, new Vector2(120 + cycle * 10, 80));
                        view.SetSize = new Vector2(320, 240);
                    }
                    if (window == pilot) pilot.SwitchMode((ShuttleConsoleWindow.ShuttleConsoleMode) cycle);
                    panel.SwitchToNative();
                    var available = window == handheld ? new Vector2(560, 580) : new Vector2(1200, 900);
                    // Apply queued child layout changes before measuring the window in this synchronous test.
                    foreach (var control in Descendants(window).Reverse()) control.InvalidateMeasure();
                    window.InvalidateMeasure();
                    window.Measure(available);
                    window.Arrange(UIBox2.FromDimensions(Vector2.Zero, available));
                    for (var i = 0; i < views.Length; i++)
                    {
                        var view = views[i];
                        Assert.That(view.SetSize, Is.EqualTo(sizes[i]), "Native radar size must survive: " + window.GetType().Name);
                        Assert.That(view.GetValue<float>(LayoutContainer.MarginLeftProperty), Is.Zero);
                        Assert.That(view.GetValue<float>(LayoutContainer.MarginTopProperty), Is.Zero);
                        Assert.That(view.GetValue<float>(LayoutContainer.MarginRightProperty), Is.Zero);
                        Assert.That(view.GetValue<float>(LayoutContainer.MarginBottomProperty), Is.Zero);
                        Assert.That(view.FitViewport, Is.EqualTo(fits[i]));
                        if (cycle == 0) parents.Add(view, view.Parent!);
                        else Assert.That(view.Parent, Is.SameAs(parents[view]));
                    }
                    var active = views.Where(view => view.VisibleInTree).ToArray();
                    Assert.That(active.Length, Is.EqualTo(1), "Native console must display its selected radar: " + window.GetType().Name);
                    Assert.That(active[0].Size.X, Is.GreaterThan(100), window.GetType().Name + " cycle " + cycle);
                    Assert.That(active[0].Size.Y, Is.GreaterThan(100));
                    Assert.That(panel.SwitchToTgui(), Is.True);
                }
#pragma warning disable CS0618
                window.Dispose();
#pragma warning restore CS0618
            }
        });
        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control root) =>
        root.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
