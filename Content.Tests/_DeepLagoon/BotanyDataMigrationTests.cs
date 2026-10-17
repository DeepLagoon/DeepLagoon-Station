using System;
using System.IO;
using System.Linq;
using Robust.Shared.GameObjects;
using Content.Shared.Botany.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.Tools.Components;
using NUnit.Framework;
using Moq;
using Robust.Shared.Network;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class BotanyDataMigrationTests : ContentUnitTest
{
    protected override Type[] ExtraComponents => typeof(PlantComponent).Assembly.GetTypes()
        .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract
            && type.Namespace?.StartsWith("Content.Shared.Botany", StringComparison.Ordinal) == true)
        .Append(typeof(ToolRefinableComponent))
        .ToArray();

    protected override void OverrideIoC()
    {
        base.OverrideIoC();
        var net = new Mock<INetManager>();
        net.SetupGet(manager => manager.IsServer).Returns(true);
        IoCManager.Instance!.RegisterInstance<INetManager>(net.Object, overwrite: true);
    }

    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [TestCase("Hydroponics/randomMutations.yml")]
    [TestCase("_DeepLagoon/Hydroponics/reagents.yml")]
    public void BotanicalEffectsDeserializeWithCurrentEngine(string relativePath)
    {
        var yaml = ReadPrototypes(relativePath);
        var serializer = IoCManager.Resolve<ISerializationManager>();
        var count = 0;
        foreach (YamlMappingNode prototype in (YamlSequenceNode) yaml.Documents[0].RootNode)
        {
            var node = new MappingDataNode(prototype);
            switch (prototype.GetNode("type").AsString())
            {
                case "randomPlantMutationList":
                    var mutations = serializer.Read<RandomPlantMutationListPrototype>(node);
                    Assert.That(mutations.Mutations, Is.Not.Empty);
                    foreach (var mutation in mutations.Mutations)
                        Assert.That(mutation.Effect, Is.Not.Null, mutation.Name);
                    count++;
                    break;
                case "reagent":
                    var reagent = serializer.Read<ReagentPrototype>(node);
                    Assert.That(reagent.ID, Is.Not.Empty);
                    count++;
                    break;
            }
        }
        Assert.That(count, Is.GreaterThan(0));
    }

    [TestCase("Reagents/botany.yml")]
    [TestCase("Reagents/elements.yml")]
    [TestCase("Reagents/medicine.yml")]
    [TestCase("Reagents/narcotics.yml")]
    [TestCase("Reagents/toxins.yml")]
    [TestCase("Reagents/Consumable/Drink/drinks.yml")]
    public void ReagentPlantMetabolismEffectsDeserialize(string relativePath)
    {
        var manager = IoCManager.Resolve<ISerializationManager>();
        var count = 0;
        foreach (YamlMappingNode prototype in (YamlSequenceNode) ReadPrototypes(relativePath).Documents[0].RootNode)
        {
            if (!prototype.Children.TryGetValue(new YamlScalarNode("plantMetabolism"), out var effects))
                continue;
            // Read the ported field in isolation: unrelated metabolism requires the damage prototype catalog.
            var parsed = manager.Read<EntityEffect[]>(new SequenceDataNode((YamlSequenceNode) effects));
            Assert.That(parsed, Has.Length.EqualTo(((YamlSequenceNode) effects).Children.Count));
            Assert.That(parsed, Has.None.Null);
            count += parsed.Length;
        }
        Assert.That(count, Is.GreaterThan(0));
    }

    [TestCase("Papercane", "SheetPaper1", 2)]
    [TestCase("Log", "MaterialWoodPlank1", 2)]
    [TestCase("SteelLog", "SheetSteel1", 1)]
    [TestCase("CottonBol", "MaterialCotton1", 2)]
    [TestCase("PyrottonBol", "MaterialPyrotton1", 2)]
    public void MaterialProduceUsesRegisteredRefiningComponent(string id, string material, int amount)
    {
        var yaml = ReadPrototypes("Entities/Objects/Consumable/Food/produce.yml");
        var prototypes = ((YamlSequenceNode) yaml.Documents[0].RootNode).Children.Cast<YamlMappingNode>();
        var prototype = prototypes.Single(node => node.GetNode("id").AsString() == id);
        var components = ((YamlSequenceNode) prototype.GetNode("components")).Children.Cast<YamlMappingNode>().ToArray();
        Assert.That(components.Select(node => node.GetNode("type").AsString()), Does.Not.Contain("Log"));

        var component = components.Single(node => node.GetNode("type").AsString() == "ToolRefinable");
        Assert.That(IoCManager.Resolve<IComponentFactory>().GetRegistration("ToolRefinable").Type,
            Is.EqualTo(typeof(ToolRefinableComponent)));
        var refine = IoCManager.Resolve<ISerializationManager>().Read<ToolRefinableComponent>(new MappingDataNode(component));
        Assert.Multiple(() =>
        {
            Assert.That(refine.QualityNeeded.Id, Is.EqualTo("Slicing"));
            Assert.That(refine.RefineFuel, Is.Zero, "Cutting produce must not require welder fuel.");
            Assert.That(refine.RefineResult, Has.Count.EqualTo(1));
            Assert.That(refine.RefineResult[0].PrototypeId?.Id, Is.EqualTo(material));
            Assert.That(refine.RefineResult[0].Amount, Is.EqualTo(amount));
        });
    }

    [TestCase("Hydroponics/plants.yml")]
    [TestCase("_DeepLagoon/Hydroponics/plants.yml")]
    [TestCase("Entities/Structures/hydro_tray.yml")]
    [TestCase("Entities/Objects/Specific/Hydroponics/seeds.yml")]
    public void BotanicalComponentFieldsDeserialize(string relativePath)
    {
        var manager = IoCManager.Resolve<ISerializationManager>();
        var factory = IoCManager.Resolve<IComponentFactory>();
        var componentTypes = ExtraComponents.ToHashSet();
        var count = 0;
        foreach (YamlMappingNode prototype in (YamlSequenceNode) ReadPrototypes(relativePath).Documents[0].RootNode)
        {
            // Abstract templates may deliberately omit required fields supplied by their children.
            if (prototype.Children.TryGetValue(new YamlScalarNode("abstract"), out var abstractValue)
                && abstractValue.AsString() == "true")
                continue;
            if (!prototype.Children.TryGetValue(new YamlScalarNode("components"), out var components))
                continue;
            foreach (YamlMappingNode component in (YamlSequenceNode) components)
            {
                var name = component.GetNode("type").AsString();
                if (!factory.TryGetRegistration(name, out var registration) || !componentTypes.Contains(registration.Type))
                    continue;
                Assert.DoesNotThrow(() => manager.Read(registration.Type, new MappingDataNode(component)),
                    $"{relativePath}: {prototype.GetNode("id").AsString()}: {name}");
                count++;
            }
        }
        Assert.That(count, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"Deserialized {count} botanical component definitions from {relativePath}.");
    }

    private static YamlStream ReadPrototypes(string relativePath)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Prototypes")))
            directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "Station resource directory was not found.");
        using var reader = File.OpenText(Path.Combine(directory!.FullName, "Resources", "Prototypes", relativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return yaml;
    }
}
