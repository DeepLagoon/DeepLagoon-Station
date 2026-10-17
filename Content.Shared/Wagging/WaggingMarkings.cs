using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Robust.Shared.Prototypes;

namespace Content.Shared.Wagging;

/// <summary>Only paired tail markings have an authored wagging animation.</summary>
public static class WaggingMarkings
{
    public const string Suffix = "Animated";

    public static bool TryGetPair(IPrototypeManager prototypes, string id, string suffix,
        out string stationary, out string animated)
    {
        stationary = id.EndsWith(suffix, StringComparison.Ordinal) ? id[..^suffix.Length] : id;
        animated = stationary + suffix;
        return prototypes.TryIndex<MarkingPrototype>(stationary, out var normal)
            && normal.BodyPart == HumanoidVisualLayers.Tail
            && normal.MarkingCategory == MarkingCategories.Tail
            && prototypes.TryIndex<MarkingPrototype>(animated, out var wagging)
            && wagging.BodyPart == HumanoidVisualLayers.Tail
            && wagging.MarkingCategory == MarkingCategories.Tail;
    }
}
