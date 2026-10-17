using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Content.Shared.Botany.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using YamlDotNet.RepresentationModel;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class PortedPrototypeRegistrationTests
{
    private readonly List<(string File, YamlMappingNode Node)> _entities = new();

    [OneTimeSetUp]
    public void ReadEntities()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Resources", "Prototypes")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "Resources", "Prototypes"), "*.yml", SearchOption.AllDirectories))
        {
            using var reader = File.OpenText(file);
            var yaml = new YamlStream();
            yaml.Load(reader);
            foreach (var document in yaml.Documents)
            {
                if (document.RootNode is not YamlSequenceNode sequence)
                    continue;
                foreach (var node in sequence.Children.OfType<YamlMappingNode>())
                {
                    if (Value(node, "type") == "entity")
                        _entities.Add((file, node));
                }
            }
        }
    }

    [Test]
    public void EntityComponentsHaveRegisteredOrExplicitlyIgnoredNames()
    {
        var assemblies = new[] { typeof(PlantComponent).Assembly, typeof(Content.Client.Entry.EntryPoint).Assembly,
            typeof(Content.Server.Entry.EntryPoint).Assembly, typeof(Component).Assembly,
            Assembly.Load("Robust.Client"), Assembly.Load("Robust.Server") };
        var calculateName = typeof(Component).Assembly.GetType("Robust.Shared.GameObjects.ComponentFactory")!
            .GetMethod("CalculateComponentName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var names = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => Attribute.IsDefined(type, typeof(RegisterComponentAttribute)))
            .Select(type => (string)calculateName.Invoke(null, new object[] { type })!)
            .Concat(Content.Server.Entry.IgnoredComponents.List).ToHashSet();
        var missing = new List<string>();
        foreach (var (file, node) in _entities)
        foreach (var component in Components(node))
        {
            var name = Value(component, "type");
            // Server.EntryPoint explicitly ignores unknown visualizer components by this suffix.
            if (name == null || (!names.Contains(name) && !name.EndsWith("Visuals", StringComparison.Ordinal)))
                missing.Add($"{file}: {Value(node, "id")}: {name}");
        }
        Assert.That(missing, Is.Empty, string.Join(Environment.NewLine, missing.Take(50)));
        TestContext.Out.WriteLine($"Checked component names in {_entities.Count} entity prototypes.");
    }

    [Test]
    public void PlantPacketsProductsAndMutationsReferenceExistingEntities()
    {
        var ids = _entities.Select(entity => Value(entity.Node, "id")).ToHashSet();
        var checkedReferences = 0;
        foreach (var (file, node) in _entities)
        foreach (var component in Components(node))
        {
            var kind = Value(component, "type");
            var fields = kind == "PlantData" ? new[] { "packetPrototype", "productPrototypes", "mutationPrototypes" }
                : kind == "PlantTray" ? new[] { "initialPlant" }
                : kind is "Seed" or "Produce" or "BotanySwab" ? new[] { "plantId" } : Array.Empty<string>();
            foreach (var field in fields)
            {
                if (!component.Children.TryGetValue(new YamlScalarNode(field), out var value))
                    continue;
                var references = value is YamlSequenceNode sequence ? sequence.Children : new[] { value }.ToList();
                foreach (var reference in references.OfType<YamlScalarNode>())
                {
                    Assert.That(ids, Does.Contain(reference.Value), $"{file}: {Value(node, "id")}.{field}");
                    checkedReferences++;
                }
            }
        }
        Assert.That(checkedReferences, Is.GreaterThan(50));
        TestContext.Out.WriteLine($"Checked {checkedReferences} plant entity references.");
    }

    private static string Value(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    private static IEnumerable<YamlMappingNode> Components(YamlMappingNode node) =>
        node.Children.TryGetValue(new YamlScalarNode("components"), out var components) && components is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>() : Array.Empty<YamlMappingNode>();
}
