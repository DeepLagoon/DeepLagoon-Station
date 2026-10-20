using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server._DeepLagoon.Admin;
using NUnit.Framework;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server;

[TestFixture]
public sealed class AdminMapUploadCompatibilityTests
{
    private static MappingDataNode Parse(string yaml) =>
        (MappingDataNode) DataNodeParser.ParseYamlStream(new StringReader(yaml)).Single().Root;

    [Test]
    public void MissingParentsKeepUidTransformAndKnownChildrenButLoseForeignLogic()
    {
        var data = Parse("""
            meta: {format: 7}
            entities:
            - proto: ForeignContainer
              entities:
              - uid: 2
                missingComponents: [AdminMapMissingPrototype]
                components:
                - type: Transform
                  parent: 1
                  pos: 2,3
                  rot: 45
                  anchored: true
                - type: ForeignExplosive
                  contents: [3]
            - proto: Wrench
              entities:
              - uid: 3
                components:
                - type: Transform
                  parent: 2
            """);
        AdminMapUploadCompatibility.ReplaceMissing(data, id => id == "Wrench", new Dictionary<string, string>());
        var groups = data.Get<SequenceDataNode>("entities");
        var missing = (MappingDataNode) groups[0];
        var marker = missing.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().Single();
        var components = marker.Get<SequenceDataNode>("components").Cast<MappingDataNode>().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(missing.Get<ValueDataNode>("proto").Value, Is.Empty);
            Assert.That(marker.Get<ValueDataNode>("uid").AsInt(), Is.EqualTo(2));
            Assert.That(marker.ContainsKey("missingComponents"), Is.False);
            Assert.That(components.Select(c => c.Get<ValueDataNode>("type").Value),
                Is.EqualTo(new[] { "Transform", "AdminMapMissingPrototype" }));
            Assert.That(components[0].Get<ValueDataNode>("pos").Value, Is.EqualTo("2,3"));
            Assert.That(components[0].Get<ValueDataNode>("rot").Value, Is.EqualTo("45"));
            Assert.That(components[0].Get<ValueDataNode>("anchored").AsBool(), Is.False);
            Assert.That(components[1].Get<ValueDataNode>("prototype").Value, Is.EqualTo("ForeignContainer"));
            var known = (MappingDataNode) groups[1];
            Assert.That(known.Get<ValueDataNode>("proto").Value, Is.EqualTo("Wrench"));
            var child = known.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().Single();
            var transform = child.Get<SequenceDataNode>("components").Cast<MappingDataNode>().Single();
            Assert.That(transform.Get<ValueDataNode>("parent").AsInt(), Is.EqualTo(2));
        });
    }

    [TestCase(3)]
    [TestCase(6)]
    [TestCase(7)]
    public void SupportedLayoutsAndValidRenamesArePreserved(int version)
    {
        var entities = version == 3
            ? "- uid: 1\n  type: Missing\n- uid: 2\n  type: OldWrench\n"
            : "- proto: Missing\n  entities: [{uid: 1}]\n- proto: OldWrench\n  entities: [{uid: 2}]\n";
        var data = Parse($"meta: {{format: {version}}}\nentities:\n{entities}");
        AdminMapUploadCompatibility.ReplaceMissing(data, id => id == "Wrench",
            new Dictionary<string, string> { ["OldWrench"] = "Wrench" });
        var groups = data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().ToArray();
        var key = version == 3 ? "type" : "proto";
        Assert.That(groups[0].Get<ValueDataNode>(key).Value, Is.Empty);
        Assert.That(groups[1].Get<ValueDataNode>(key).Value, Is.EqualTo("OldWrench"));
    }

    [Test]
    public void UnknownGridPrototypeKeepsNativeGridGeometry()
    {
        var data = Parse("""
            meta: {format: 7}
            entities:
            - proto: ForeignShip
              entities:
              - uid: 1
                components:
                - type: MapGrid
                  chunks: {}
                - type: ForeignShuttleController
            """);
        AdminMapUploadCompatibility.ReplaceMissing(data, _ => false, new Dictionary<string, string>());
        var group = data.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().Single();
        var entity = group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>().Single();
        var types = entity.Get<SequenceDataNode>("components").Cast<MappingDataNode>()
            .Select(component => component.Get<ValueDataNode>("type").Value);
        Assert.That(types, Is.EqualTo(new[] { "MapGrid", "AdminMapMissingPrototype" }));
    }
}
