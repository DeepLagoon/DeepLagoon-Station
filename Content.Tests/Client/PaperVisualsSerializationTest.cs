using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Content.Client.Paper.UI;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using Robust.UnitTesting;
using YamlDotNet.RepresentationModel;

namespace Content.Tests.Client;

[TestFixture]
public sealed class PaperVisualsSerializationTest : RobustUnitTest
{
    public override UnitTestProject Project => UnitTestProject.Client;
    protected override Type[] ExtraComponents => [typeof(PaperVisualsComponent)];
    protected override Assembly[] GetContentAssemblies() =>
        [typeof(PaperVisualsComponent).Assembly, typeof(Content.Shared.Entry.EntryPoint).Assembly];

    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase("216,0,0,0", 216, 0, 0, 0)]
    [TestCase("23,16,14,15", 23, 15, 14, 16)]
    [TestCase("0,12,0,0", 0, 0, 0, 12)]
    [TestCase("16,16,16,0", 16, 0, 16, 16)]
    public void AsymmetricMarginsPreserveSidesAndRoundTrip(string source, float left, float top, float right, float bottom)
    {
        var serializer = new PaperMarginSerializer();
        var manager = IoCManager.Resolve<ISerializationManager>();
        var dependencies = IoCManager.Instance!;
        var margin = serializer.Read(manager, new ValueDataNode(source), dependencies, default);
        Assert.That(margin, Is.EqualTo(new Thickness(left, top, right, bottom)));
        var written = (ValueDataNode) serializer.Write(manager, margin, dependencies);
        Assert.That(written.Value, Is.EqualTo(source));
    }

    [Test]
    public void AllPaperVisualPrototypesDeserializeWithoutChangingMargins()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Prototypes")))
            directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "Station resource directory was not found.");
        var manager = IoCManager.Resolve<ISerializationManager>();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory!.FullName, "Resources", "Prototypes"), "*.yml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("type: PaperVisuals", StringComparison.Ordinal))
                continue;
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            foreach (YamlMappingNode prototype in (YamlSequenceNode) yaml.Documents[0].RootNode)
            {
                if (!prototype.Children.TryGetValue(new YamlScalarNode("components"), out var components))
                    continue;
                foreach (YamlMappingNode component in (YamlSequenceNode) components)
                {
                    if (component.GetNode("type").AsString() != "PaperVisuals")
                        continue;
                    var visuals = manager.Read<PaperVisualsComponent>(new MappingDataNode(component));
                    CheckMargin("backgroundPatchMargin", visuals.BackgroundPatchMargin);
                    CheckMargin("headerMargin", visuals.HeaderMargin);
                    CheckMargin("contentMargin", visuals.ContentMargin);
                    count++;

                    void CheckMargin(string field, Thickness actual)
                    {
                        if (!component.Children.TryGetValue(new YamlScalarNode(field), out var node))
                            return;
                        var values = node.AsString().Split(',').Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                        Assert.That(actual, Is.EqualTo(new Thickness(values[0], values[3], values[2], values[1])),
                            $"{file}: {prototype.GetNode("id").AsString()}.{field}");
                    }
                }
            }
        }
        Assert.That(count, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"Deserialized {count} PaperVisuals components.");
    }
}
