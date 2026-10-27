using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.WebUI;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class InterfaceStyleTest
{
    [TestCase(0, false, false)]
    [TestCase(1, true, true)]
    [TestCase(2, true, false)]
    [TestCase(-1, false, false)]
    [TestCase(99, false, false)]
    public void OnlyExplicitChoicesDismissOnboarding(int style, bool chosen, bool tgui)
    {
        Assert.That(WebUiCVars.HasChosenStyle(style), Is.EqualTo(chosen));
        Assert.That(WebUiCVars.UseTgui(style), Is.EqualTo(tgui));
    }

    [Test]
    public void WindowChoicesRemainIndependentAndOverrideBothGlobalDefaults()
    {
        var saved = WindowInterfaceStyle.WithChoice("", "LatheConsole", true);
        saved = WindowInterfaceStyle.WithChoice(saved, "VendingMachine", false);
        Assert.That(WindowInterfaceStyle.Resolve(saved, "LatheConsole", 2), Is.True);
        Assert.That(WindowInterfaceStyle.Resolve(saved, "VendingMachine", 1), Is.False);
        Assert.That(WindowInterfaceStyle.Resolve(saved, "BatteryStorage", 1), Is.True);
        Assert.That(WindowInterfaceStyle.Resolve(saved, "BatteryStorage", 2), Is.False);
        saved = WindowInterfaceStyle.WithChoice(saved, "LatheConsole", false);
        Assert.That(saved.Split(';').Length, Is.EqualTo(2));
        Assert.That(WindowInterfaceStyle.Resolve(saved, "LatheConsole", 1), Is.False);
        Assert.That(WindowInterfaceStyle.Resolve(saved, "VendingMachine", 1), Is.False);
        Assert.That(WindowInterfaceStyle.Resolve("LatheConsoleExtra=1;broken;LatheConsole=99", "LatheConsole", 2), Is.False);
    }

    [Test]
    public void EveryProductionTguiHasANativeView()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "TGUI", "packages"))) directory = directory.Parent;
        Assert.That(directory, Is.Not.Null);
        // Interfaces that restore the original strongly typed SS14 window.
        var typed = new HashSet<string> { "CharacterEditor", "CharacterCatalog", "CharacterColorPicker", "ChatComposer", "ShuttleConsole", "ShipRadarConsole", "GhostRoles", "Jukebox", "VendingMachine", "LatheConsole", "ResearchConsole", "BatteryStorage", "PortableGenerator", "DiskConsole" };
        var names = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory!.FullName, "Content.Client"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "(?:Set(?:Incremental)?State\\(|const string InterfaceName = )\"([^\"]+)\""))
            {
                names.Add(match.Groups[1].Value);
                if (typed.Contains(match.Groups[1].Value))
                    Assert.That(source.Contains("SetNativeFallback") || source.Contains("new CivilianConsoleHost"), Is.True, $"{file} must wire the native counterpart");
            }
        }
        Assert.That(names.Count, Is.GreaterThan(15));
        Assert.That(names.Where(name => !typed.Contains(name) && !NativeInterfacePanel.Supports(name)), Is.Empty,
            "New TGUI interfaces require a native view or a registered typed fallback.");
    }

    [Test]
    public void NativeStatePreservesTextAndRejectsNonFiniteNumbers()
    {
        var data = NativeUiData.Parse("{\"name\":\"Игрок <тест>\",\"pending\":true,\"items\":[{\"id\":\"a\"}],\"x\":\"NaN\"}");
        Assert.That(data.String("name"), Is.EqualTo("Игрок <тест>"));
        Assert.That(data.Bool("pending"), Is.True);
        Assert.That(data.Array("items").Single().String("id"), Is.EqualTo("a"));
        Assert.That(data.Float("x"), Is.Zero);
    }
}
