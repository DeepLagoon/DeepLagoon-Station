using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.BodyCustomization;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private IEnumerable<TguiData> BodyCustomizationData()
    {
        var features = BodyCustomizationCatalog.SanitizeForEditor(Profile?.Appearance.BodyFeatures);
        return BodyCustomizationCatalog.Order.Where(part => part != "womb").Select(part =>
        {
            var feature = features.FirstOrDefault(f => f.Part == part) ?? BodyCustomizationCatalog.Default(part);
            return new TguiData().String("part", part).String("name", BodyCustomizationCatalog.Names[part])
                .Bool("enabled", feature.Enabled)
                .Bool("showThroughClothing", feature.ShowThroughClothing)
                .Bool("womb", features.Any(f => f.Part == "womb" && f.Enabled))
                .Bool("anus", features.Any(f => f.Part == "anus" && f.Enabled))
                .String("shape", feature.Shape).String("size", feature.Size)
                .Bool("matchSkin", feature.MatchSkin).String("color", feature.Color).Number("visibility", feature.Visibility)
                .Bool("secondaryEnabled", feature.SecondaryEnabled).String("secondaryColor", feature.SecondaryColor)
                .Bool("secondaryAvailable", part == "breasts" && feature.Shape == "pair" && BodyCustomizationCatalog.BreastSecondarySizes.Contains(feature.Size))
                .Array("shapes", (part == "womb" ? new[] { "internal" } : BodyCustomizationCatalog.Parts[part].Keys.ToArray())
                    .Select(s => Choice(s, BodyShapeName(s))))
                .Array("sizes", BodyCustomizationCatalog.EditorSizes(part, feature.Shape).Select(s => Choice(s, s.ToUpperInvariant())));
        });
    }

    private static string BodyShapeName(string shape) => shape switch
    {
        "pair" => "Обычная", "quad" => "Две пары", "sextuple" => "Три пары", "human" => "Человеческая",
        "single" => "Обычная", "sheath" => "В оболочке", "hidden" => "Скрытая", "donut" => "Обычная",
        "squished" => "Приплюснутая", "knotted" => "С узлом", "barbknot" => "Шипастая, с узлом",
        "flared" => "Расширенная", "tentacle" => "Щупальце", "hemi" => "Двойная", "hemiknot" => "Двойная, с узлом",
        "tapered" => "Коническая", "thick" => "Широкая", "barbtapered" => "Шипастая, коническая",
        "bhemiknot" => "Шипастая двойная, с узлом", "gaping" => "Расширенная", "dentata" => "Зубчатая",
        "hairy" => "С волосами", "furred" => "С мехом", "spade" => "Листовидная", "cloaca" => "Клоака",
        "sergal" => "Сергальская", "cloaca_teshari" => "Клоака тешари", "internal" => "Внутренняя", _ => shape,
    };

    private bool HandleBodyCustomizationAction(TguiActionData args)
    {
        if (Profile == null || args.String("part") is not { } part || part == "womb" || !BodyCustomizationCatalog.Names.ContainsKey(part)) return false;
        var features = BodyCustomizationCatalog.SanitizeForEditor(Profile.Appearance.BodyFeatures);
        var feature = features.FirstOrDefault(f => f.Part == part);
        if (feature == null) { feature = BodyCustomizationCatalog.Default(part); features.Add(feature); }
        var value = args.String("value") ?? "";
        switch (args.String("field"))
        {
            case "enabled":
                if (!bool.TryParse(value, out var enabled)) return false;
                feature.Enabled = enabled; break;
            case "showThroughClothing":
                if (!bool.TryParse(value, out var showThroughClothing)) return false;
                feature.ShowThroughClothing = showThroughClothing; break;
            case "womb":
                if (part != "vagina" || !bool.TryParse(value, out var hasWomb)) return false;
                var womb = features.FirstOrDefault(f => f.Part == "womb");
                if (womb == null) { womb = BodyCustomizationCatalog.Default("womb"); features.Add(womb); }
                womb.Enabled = hasWomb;
                break;
            case "anus":
                if (part != "butt" || !bool.TryParse(value, out var hasAnus)) return false;
                var anus = features.FirstOrDefault(f => f.Part == "anus");
                if (anus == null) { anus = BodyCustomizationCatalog.Default("anus"); features.Add(anus); }
                anus.Enabled = hasAnus;
                break;
            case "shape":
                if (part is "womb" or "anus" || !BodyCustomizationCatalog.Parts[part].ContainsKey(value)) return false;
                feature.Shape = value;
                if (!BodyCustomizationCatalog.Parts[part][value].ContainsKey(feature.Size))
                    feature.Size = BodyCustomizationCatalog.Sizes(part, value).First();
                break;
            case "size":
                if (part == "anus" || !BodyCustomizationCatalog.EditorSizes(part, feature.Shape).Contains(value)) return false;
                feature.Size = value; break;
            case "matchSkin":
                if (!bool.TryParse(value, out var matchSkin)) return false;
                feature.MatchSkin = matchSkin; break;
            case "color":
                if (!Color.TryFromHex(value, out var color)) return false;
                feature.Color = color.ToHexNoAlpha(); break;
            case "secondaryEnabled":
                if (part != "breasts" || !bool.TryParse(value, out var secondaryEnabled)) return false;
                feature.SecondaryEnabled = secondaryEnabled; break;
            case "secondaryColor":
                if (part != "breasts" || !Color.TryFromHex(value, out var secondaryColor)) return false;
                feature.SecondaryColor = secondaryColor.ToHexNoAlpha(); break;
            case "visibility":
                if (!args.TryInt("value", out var visibility) || visibility is < 0 or > 2) return false;
                feature.Visibility = visibility; break;
            default: return false;
        }
        var appearance = Profile.Appearance.Clone();
        appearance.BodyFeatures = BodyCustomizationCatalog.SanitizeForEditor(features);
        Profile = Profile.WithCharacterAppearance(appearance);
        ReloadProfilePreview();
        SetDirty();
        PublishTguiProfile();
        return true;
    }
}
