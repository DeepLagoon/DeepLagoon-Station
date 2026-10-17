using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Content.Client.Shuttles.UI;
using Content.Client.UserInterface.Screens;
using NUnit.Framework;

namespace Content.Tests.Client;

[TestFixture]
public sealed class ClientXamlCompilationTests
{
    [Test]
    public void EveryClientXamlFileHasCompiledPopulateMethod()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Content.Client", "UserInterface")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var client = Path.Combine(root!.FullName, "Content.Client");
        var assembly = typeof(DefaultGameScreen).Assembly;
        var files = Directory.GetFiles(client, "*.xaml", SearchOption.AllDirectories);
        Assert.That(files.Length, Is.GreaterThan(100));
        foreach (var file in files)
        {
            var resource = "Content.Client." + Path.GetRelativePath(client, file).Replace(Path.DirectorySeparatorChar, '.');
            var document = XDocument.Load(file);
            var className = document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                ?? resource[..^5];
            var control = assembly.GetType(className);
            Assert.That(control, Is.Not.Null, file);
            Assert.That(control!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(method => method.Name == $"Populate:{resource}"), Is.True, file);
        }
        TestContext.Out.WriteLine($"Verified compiled XAML for {files.Length} files.");
    }

    [TestCase(typeof(DefaultGameScreen))]
    [TestCase(typeof(SeparatedChatGameScreen))]
    [TestCase(typeof(ShuttleDockControl))]
    public void ClientAssemblyContainsCompiledXaml(Type controlType)
    {
        var populate = controlType.GetMethod($"Populate:{controlType.FullName}.xaml",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.That(populate, Is.Not.Null,
            $"{controlType.FullName} needs compiled XAML. Rebuild Content.Client with resource preparation; /t:Compile alone is insufficient.");
    }
}
