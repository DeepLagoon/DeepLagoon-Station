using System.Linq;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._DeepLagoon.Admin;

/// <summary>Only local admin uploads tolerate missing prototypes; normal map loading remains strict.</summary>
public static class AdminMapUploadCompatibility
{
    public static void ReplaceMissing(MappingDataNode data, Func<string, bool> exists,
        IReadOnlyDictionary<string, string> renamed)
    {
        var version = data.Get<MappingDataNode>("meta").Get<ValueDataNode>("format").AsInt();
        var groups = data.Get<SequenceDataNode>("entities");
        foreach (var group in groups.Cast<MappingDataNode>())
        {
            var key = version < 4 ? "type" : "proto";
            if (!group.TryGet<ValueDataNode>(key, out var prototype) || string.IsNullOrWhiteSpace(prototype.Value))
                continue;
            var original = prototype.Value;
            var resolved = renamed.TryGetValue(original, out var replacement) ? replacement : original;
            if (exists(resolved))
                continue;

            // Blank is a built-in, prototype-free entity. Preserve all YAML UIDs so
            // valid objects inside/parented to a missing object are not deleted too.
            group[key] = new ValueDataNode(string.Empty);
            if (version < 4)
                ReplaceEntity(group, original);
            else
                foreach (var entity in group.Get<SequenceDataNode>("entities").Cast<MappingDataNode>())
                    ReplaceEntity(entity, original);
        }
    }

    private static void ReplaceEntity(MappingDataNode entity, string prototype)
    {
        var components = new SequenceDataNode();
        if (entity.TryGet<SequenceDataNode>("components", out var existing))
        {
            foreach (var component in existing.Cast<MappingDataNode>())
            {
                if (!component.TryGet<ValueDataNode>("type", out var type) ||
                    type.Value is not ("Transform" or "MapGrid" or "Map"))
                    continue;
                // These markers have no physics, containers, spawners or foreign logic.
                // Keep native map/grid geometry when the missing prototype is a grid root.
                if (type.Value == "Transform")
                    component["anchored"] = new ValueDataNode("false");
                components.Add(component);
            }
        }
        components.Add(new MappingDataNode
        {
            { "type", new ValueDataNode("AdminMapMissingPrototype") },
            { "prototype", new ValueDataNode(prototype) },
        });
        entity["components"] = components;
        entity.Remove("missingComponents");
    }
}
