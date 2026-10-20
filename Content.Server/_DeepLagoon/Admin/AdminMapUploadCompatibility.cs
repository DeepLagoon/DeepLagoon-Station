using System.Linq;
using System.Text.RegularExpressions;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._DeepLagoon.Admin;

/// <summary>Only local admin uploads tolerate missing prototypes; normal map loading remains strict.</summary>
public static class AdminMapUploadCompatibility
{
    /// <summary>Resolve foreign tile names only for admin uploads, preserving numeric tile IDs and chunk data.</summary>
    public static Dictionary<string, string> ReplaceMissingTiles(MappingDataNode data,
        Func<string, bool> exists, IEnumerable<string> available)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!data.TryGet<MappingDataNode>("tilemap", out var tilemap))
            return replacements;

        // Never turn a missing floor into a hole. Sort ties so registration order cannot change the result.
        var candidates = available.Where(id => id != "Space").Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => (Id: id, Words: TileWords(id))).ToArray();
        foreach (var (key, value) in tilemap.Children.ToArray())
        {
            var original = ((ValueDataNode) value).Value;
            // This also preserves registered aliases, before the engine applies its normal tile migrations.
            if (exists(original))
                continue;
            if (!replacements.TryGetValue(original, out var replacement))
            {
                var words = TileWords(original);
                var bestScore = double.NegativeInfinity;
                replacement = null;
                foreach (var candidate in candidates)
                {
                    // Whole name words carry more weight than character resemblance, so a fork/location
                    // prefix cannot outweigh a matching material (e.g. RMCPlanetHybrisaAsphalt).
                    var total = words.Count + candidate.Words.Count;
                    var score = total == 0 ? 0 : 2.0 * words.Intersect(candidate.Words).Count() / total;
                    score += 0.2 * NameSimilarity(original, candidate.Id);
                    if (score <= bestScore)
                        continue;
                    bestScore = score;
                    replacement = candidate.Id;
                }
                if (replacement == null)
                    throw new InvalidOperationException("No non-space tiles are registered for admin map upload.");
                replacements.Add(original, replacement);
            }
            tilemap[key] = new ValueDataNode(replacement);
        }
        return replacements;
    }

    private static HashSet<string> TileWords(string name) => Regex.Matches(name,
            "[A-Z]+(?=[A-Z][a-z]|[^a-zA-Z]|$)|[A-Z]?[a-z]+|[0-9]+")
        .Select(match => match.Value.ToLowerInvariant())
        .Where(word => word is not ("floor" or "tile" or "planet"))
        .ToHashSet(StringComparer.Ordinal);

    private static double NameSimilarity(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();
        var row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var previous = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1),
                    diagonal + (left[i - 1] == right[j - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return 1.0 - (double) row[right.Length] / Math.Max(left.Length, right.Length);
    }

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
