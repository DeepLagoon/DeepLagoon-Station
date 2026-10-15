using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.BodyCustomization;

/// <summary>Cosmetic anatomy only. Size selects a source sprite.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class BodyFeature
{
    [DataField] public string Part { get; set; } = "";
    [DataField] public bool Enabled { get; set; }
    [DataField] public string Shape { get; set; } = "";
    [DataField] public string Size { get; set; } = "";
    // Legacy profiles may still contain scale; it no longer affects appearance.
    [DataField] public float Scale { get; set; } = 1;
    [DataField] public bool MatchSkin { get; set; } = true;
    [DataField] public string Color { get; set; } = "#FFFFFF";
    [DataField] public bool SecondaryEnabled { get; set; }
    [DataField] public string SecondaryColor { get; set; } = "#FFFFFF";
    // 0 = hidden, 1 = below clothing, 2 = above clothing.
    [DataField] public int Visibility { get; set; } = 1;

    public BodyFeature Clone() => (BodyFeature) MemberwiseClone();
    public bool ContentEquals(BodyFeature other) => Part == other.Part && Enabled == other.Enabled
        && Shape == other.Shape && Size == other.Size && Scale.Equals(other.Scale)
        && MatchSkin == other.MatchSkin && Color == other.Color && Visibility == other.Visibility
        && SecondaryEnabled == other.SecondaryEnabled && SecondaryColor == other.SecondaryColor;
}

public static partial class BodyCustomizationCatalog
{
    // Also determines stable layer order. Womb is internal and intentionally has no sprite.
    public static readonly string[] Order = ["butt", "belly", "breasts", "vagina", "anus", "testicles", "penis", "womb"];
    public static readonly Dictionary<string, string> Names = new()
    {
        ["butt"] = "Ягодицы / бёдра", ["belly"] = "Живот", ["breasts"] = "Грудь",
        ["vagina"] = "Вульва", ["anus"] = "Анус", ["testicles"] = "Яички", ["penis"] = "Пенис", ["womb"] = "Матка (внутренний орган)",
    };

    public static BodyFeature Default(string part)
    {
        if (part == "womb") return new() { Part = part, Shape = "internal", Size = "1" };
        var shape = Parts[part].Keys.First();
        return new() { Part = part, Shape = shape, Size = Sizes(part, shape).First() };
    }

    public static string[] Sizes(string part, string shape)
    {
        if (part == "womb") return ["1"];
        return Parts[part][shape].Keys.OrderBy(s => int.TryParse(s, out var n) ? n : s.Length == 1 ? s[0] :
            s switch { "huge" => 1000, "massive" => 1001, "giga" => 1002, _ => 1003 }).ToArray();
    }

    public static List<BodyFeature> Sanitize(IEnumerable<BodyFeature>? features)
    {
        var result = new List<BodyFeature>();
        if (features == null) return result;
        foreach (var feature in features.Take(32))
        {
            if (feature == null || string.IsNullOrEmpty(feature.Part) || !Names.ContainsKey(feature.Part) || result.Any(f => f.Part == feature.Part)) continue;
            var copy = feature.Clone();
            copy.Shape ??= "";
            copy.Size ??= "";
            if (copy.Part == "anus") copy.Shape = "donut";
            if (copy.Part == "womb") { copy.Shape = "internal"; copy.Size = "1"; }
            else
            {
                if (!Parts[copy.Part].TryGetValue(copy.Shape, out var sizes))
                { copy.Shape = Default(copy.Part).Shape; sizes = Parts[copy.Part][copy.Shape]; }
                if (!sizes.ContainsKey(copy.Size)) copy.Size = Sizes(copy.Part, copy.Shape).First();
            }
            copy.Scale = 1;
            copy.Visibility = Math.Clamp(copy.Visibility, 0, 2);
            copy.Color = Robust.Shared.Maths.Color.TryFromHex(copy.Color ?? "", out var color) ? color.ToHexNoAlpha() : "#FFFFFF";
            copy.SecondaryEnabled &= copy.Part == "breasts";
            copy.SecondaryColor = Robust.Shared.Maths.Color.TryFromHex(copy.SecondaryColor ?? "", out var secondary) ? secondary.ToHexNoAlpha() : "#FFFFFF";
            result.Add(copy);
        }
        return result.OrderBy(f => Array.IndexOf(Order, f.Part)).ToList();
    }

    public static bool ContentEquals(List<BodyFeature> a, List<BodyFeature> b) => a.Count == b.Count
        && a.Zip(b).All(pair => pair.First.ContentEquals(pair.Second));
}
