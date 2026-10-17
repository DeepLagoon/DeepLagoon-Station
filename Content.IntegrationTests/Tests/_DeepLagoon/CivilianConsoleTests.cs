#nullable enable
using System.Reflection;
using System.Text.Json;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Research.UI;
using Content.Client._Mono.Research.UI;
using Content.Shared._DeepLagoon.WebUI;
using Content.Shared.Research;
using Content.Shared._Mono.Research;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CivilianConsoleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Action<string, string> Actions(TguiPanel panel) =>
        (Action<string, string>)typeof(TguiPanel).GetField("OnAction", Private)!.GetValue(panel)!;

    [Test]
    public async Task DiskTerminalsRouteOnlyAvailableNativeOperations()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            pair.Client.ResolveDependency<IConfigurationManager>().SetCVar(WebUiCVars.InterfaceStyle, 1);
            using var tech = new DiskConsoleMenu();
            using var points = new PointDiskConsoleMenu();
            Assert.That(tech.ConsoleHost, Is.Not.Null);
            Assert.That(points.ConsoleHost, Is.Not.Null);
            var techAction = Actions(tech.ConsoleHost!.Panel);
            var pointAction = Actions(points.ConsoleHost!.Panel);
            var standard = 0; var rare = 0; var one = 0; var five = 0; var ten = 0; var servers = 0;
            tech.OnPrintButtonPressed += () => standard++;
            tech.OnPrintRareButtonPressed += () => rare++;
            points.OnPrint1KButtonPressed += () => one++;
            points.OnPrint5KButtonPressed += () => five++;
            points.OnPrint10KButtonPressed += () => ten++;
            tech.OnServerButtonPressed += () => servers++;
            points.OnServerButtonPressed += () => servers++;
            techAction("print", "{\"id\":\"standard\"}");
            pointAction("print", "{\"id\":\"1k\"}");
            Assert.That(standard + one, Is.Zero, "No print before the first authoritative state");
            techAction("server", "{}"); pointAction("server", "{}");
            Assert.That(servers, Is.EqualTo(2));
            tech.Update(new DiskConsoleBoundUserInterfaceState(17000, 1000, 10000, true, false));
            points.Update(new PointDiskConsoleBoundUserInterfaceState(17000, 1000, 5000, 10000, true, false, true));
            techAction("print", "{\"id\":\"standard\"}");
            techAction("print", "{\"id\":\"rare\"}");
            foreach (var id in new[] { "1k", "5k", "10k", "unknown", "standard" })
                pointAction("print", "{\"id\":\"" + id + "\"}");
            pointAction("print", "invalid");
            Assert.Multiple(() =>
            {
                Assert.That(standard, Is.EqualTo(1)); Assert.That(rare, Is.Zero);
                Assert.That(one, Is.EqualTo(1)); Assert.That(five, Is.Zero); Assert.That(ten, Is.EqualTo(1));
            });
            points.Update(new PointDiskConsoleBoundUserInterfaceState(7000, 1200, 6000, 12000, false, true, false));
            using var snapshot = JsonDocument.Parse((string)typeof(TguiPanel).GetField("_data", Private)!.GetValue(points.ConsoleHost.Panel)!);
            var entries = snapshot.RootElement.GetProperty("entries");
            Assert.That(entries[0].GetProperty("disabled").GetBoolean(), Is.True);
            Assert.That(entries[1].GetProperty("disabled").GetBoolean(), Is.False);
            Assert.That(entries[1].GetProperty("label").GetString(), Does.Contain("6000"));
            pointAction("print", "{\"id\":\"1k\"}");
            pointAction("print", "{\"id\":\"5k\"}");
            pointAction("print", "{\"id\":\"10k\"}");
            Assert.That(one, Is.EqualTo(1)); Assert.That(five, Is.EqualTo(1)); Assert.That(ten, Is.EqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FallbackRetainsTypedControlsAndDoesNotChangePlayerPreference()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 1);
            using var points = new PointDiskConsoleMenu();
            using var tech = new DiskConsoleMenu();
            var callback = Actions(points.ConsoleHost!.Panel);
            var prints = 0;
            points.OnPrint1KButtonPressed += () => prints++;
            points.Update(new PointDiskConsoleBoundUserInterfaceState(2000, 1000, 5000, 10000, true, false, false));
            points.ConsoleHost.Panel.SwitchToNative();
            tech.ConsoleHost!.Panel.SwitchToNative();
            Assert.That(points.ConsoleHost.Visible, Is.False);
            Assert.That(tech.ConsoleHost.Visible, Is.False);
            Assert.That(points.ContentsContainer.GetChild(0).Visible, Is.True);
            Assert.That(tech.ContentsContainer.GetChild(0).Visible, Is.True);
            Assert.That(cfg.GetCVar(WebUiCVars.InterfaceStyle), Is.EqualTo(1));
            callback("print", "{\"id\":\"1k\"}");
            Assert.That(prints, Is.Zero, "Queued web actions must not print after native fallback");
            var button = points.FindControl<Button>("Print1KButton");
            Assert.That(button.Disabled, Is.False);
            var press = (Action<BaseButton.ButtonEventArgs>)typeof(BaseButton).GetField("OnPressed", Private)!.GetValue(button)!;
            press(new BaseButton.ButtonEventArgs(button, null!));
            Assert.That(prints, Is.EqualTo(1), "The original native event remains connected");
            points.Update(new PointDiskConsoleBoundUserInterfaceState(0, 1000, 5000, 10000, false, false, false));
            Assert.That(button.Disabled, Is.True);
            cfg.SetCVar(WebUiCVars.InterfaceStyle, 2);
            using var classicPoints = new PointDiskConsoleMenu();
            using var classicTech = new DiskConsoleMenu();
            Assert.That(classicPoints.ConsoleHost, Is.Null);
            Assert.That(classicTech.ConsoleHost, Is.Null);
        });
        await pair.CleanReturnAsync();
    }
}
