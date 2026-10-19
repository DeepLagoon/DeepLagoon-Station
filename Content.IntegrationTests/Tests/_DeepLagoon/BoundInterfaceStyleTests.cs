using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class BoundInterfaceStyleTests
{
    [Test]
    public async Task SeparatePresentationsReuseTheRegisteredNativeWindow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, InLobby = true });
        await pair.Client.WaitAssertion(() =>
        {
            var cfg = pair.Client.ResolveDependency<IConfigurationManager>();
            var entities = pair.Client.ResolveDependency<IEntityManager>();
            var ui = entities.System<UserInterfaceSystem>();
            var registrations = (IDictionary) typeof(UserInterfaceSystem)
                .GetField("_registeredControls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ui)!;
            var savePosition = typeof(UserInterfaceSystem).GetMethod("SavePosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cases = new (string Prototype, Type Bui, string Key, string Native, string Web)[]
            {
                ("VendingMachineDinnerware", typeof(Content.Client.VendingMachines.VendingMachineBoundUserInterface), "VendingMachine", "_menu", "_tguiWindow"),
                ("Autolathe", typeof(Content.Client.Lathe.UI.LatheBoundUserInterface), "LatheConsole", "_menu", "_tguiWindow"),
                ("Jukebox", typeof(Content.Client.Audio.Jukebox.JukeboxBoundUserInterface), "Jukebox", "_menu", "_tguiWindow"),
                ("SMESBasic", typeof(Content.Client.Power.Battery.BatteryBoundUserInterface), "BatteryStorage", "_menu", "_tguiWindow"),
                ("PortableGeneratorPacman", typeof(Content.Client.Power.Generator.PortableGeneratorBoundUserInterface), "PortableGenerator", "_window", "_tguiWindow"),
                ("ComputerResearchAndDevelopment", typeof(Content.Client._DeepLagoon.Research.ResearchConsoleTguiBoundUserInterface), "ResearchConsole", "_classicWindow", "_window"),
            };
            foreach (var startTgui in new[] { false, true })
            foreach (var entry in cases)
            {
                cfg.SetCVar(WebUiCVars.WindowStyles, entry.Key + (startTgui ? "=1" : "=2"));
                var owner = entities.SpawnEntity(entry.Prototype, MapCoordinates.Nullspace);
                var interfaces = (IDictionary) typeof(UserInterfaceComponent).GetField("Interfaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entities.GetComponent<UserInterfaceComponent>(owner))!;
                var key = interfaces.Keys.Cast<Enum>().First();
                using var bui = (BoundUserInterface) Activator.CreateInstance(entry.Bui, owner, key)!;
                var open = entry.Bui.GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var change = entry.Bui.GetMethod("SwitchInterface", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var nativeField = entry.Bui.GetField(entry.Native, BindingFlags.Instance | BindingFlags.NonPublic)!;
                var webField = entry.Bui.GetField(entry.Web, BindingFlags.Instance | BindingFlags.NonPublic)!;
                open.Invoke(bui, null);
                BaseWindow native = null;
                for (var i = 0; i < 3; i++)
                {
                    foreach (var tgui in startTgui ? new[] { false, true } : new[] { true, false })
                    {
                        var oldWeb = (BaseWindow) webField.GetValue(bui);
                        cfg.SetCVar(WebUiCVars.WindowStyles, entry.Key + (tgui ? "=1" : "=2"));
                        change.Invoke(bui, new object[] { tgui });
                        Assert.That(bui.IsOpened, Is.True, entry.Key);
                        if (oldWeb != null) Assert.That(oldWeb.Disposed, Is.True, entry.Key);
                        var currentNative = (BaseWindow) nativeField.GetValue(bui);
                        if (currentNative != null)
                        {
                            native ??= currentNative;
                            Assert.That(currentNative, Is.SameAs(native), entry.Key);
                            Assert.That(native.Disposed, Is.False, entry.Key);
                            Assert.That(native.IsOpen, Is.EqualTo(!tgui), entry.Key);
                            Assert.That(registrations[bui], Is.SameAs(native), entry.Key);
                            Assert.That(Descendants(native).OfType<Button>().Count(button => button.Text == "TGUI"), Is.EqualTo(1), entry.Key);
                        }
                        Assert.That(webField.GetValue(bui) != null, Is.EqualTo(tgui), entry.Key);
                    }
                }
                var closingWindow = startTgui ? (BaseWindow) webField.GetValue(bui) : native;
                closingWindow.Close();
                Assert.That(bui.IsOpened, Is.False, "Closing either presentation must close the BUI: " + entry.Key);
                // The engine saves the registered native position when the BUI closes.
                savePosition.Invoke(ui, new object[] { bui });
                Assert.That(registrations.Contains(bui), Is.False, entry.Key);
                Assert.That(ui.TryGetPosition(owner, key, out _), Is.True, entry.Key);
                bui.Dispose();
                Assert.That(native.Disposed, Is.True, "Final BUI disposal owns the native window: " + entry.Key);
                Assert.That(closingWindow.Disposed, Is.True, entry.Key);
            }
        });
        await pair.CleanReturnAsync();
    }

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root) =>
        root.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
