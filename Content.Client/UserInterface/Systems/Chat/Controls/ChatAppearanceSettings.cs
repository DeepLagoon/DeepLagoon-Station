using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.RepresentationModel;

namespace Content.Client.UserInterface.Systems.Chat.Controls;

public sealed class ChatAppearanceSettings
{
    public string PanelColor { get; set; } = "#2B2C3B";
    public string ChatColor { get; set; } = "#20222E";
    public string GradientColor { get; set; } = "#33394E";
    public string InputColor { get; set; } = "#555B70";
    public string HighlightColor { get; set; } = "#FFD166";
    public string Font { get; set; } = "NotoSans";
    public int FontSize { get; set; } = 14;
    public string HighlightWords { get; set; } = "";
    public bool WholeWords { get; set; }
    public string Background { get; set; } = "Solid";
    public string Image { get; set; } = "";

    public YamlMappingNode ToYaml() => new()
    {
        { "PanelColor", PanelColor }, { "ChatColor", ChatColor }, { "GradientColor", GradientColor },
        { "InputColor", InputColor }, { "HighlightColor", HighlightColor }, { "Font", Font },
        { "FontSize", FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        { "HighlightWords", HighlightWords }, { "WholeWords", WholeWords ? "true" : "false" },
        { "Background", Background }
    };

    public static ChatAppearanceSettings FromYaml(MappingDataNode node)
    {
        var result = new ChatAppearanceSettings();
        string Get(string key, string fallback, int max = 32) =>
            node.TryGet<ValueDataNode>(key, out var value) && value.Value.Length <= max ? value.Value : fallback;
        string ColorValue(string key, string fallback)
        {
            var value = Get(key, fallback);
            return Color.TryFromHex(value, out _) ? value : fallback;
        }
        result.PanelColor = ColorValue("PanelColor", result.PanelColor);
        result.ChatColor = ColorValue("ChatColor", result.ChatColor);
        result.GradientColor = ColorValue("GradientColor", result.GradientColor);
        result.InputColor = ColorValue("InputColor", result.InputColor);
        result.HighlightColor = ColorValue("HighlightColor", result.HighlightColor);
        var font = Get("Font", result.Font);
        result.Font = font is "NotoSans" or "NotoSansDisplay" or "Boxfont" ? font : result.Font;
        if (int.TryParse(Get("FontSize", "14"), out var size)) result.FontSize = Math.Clamp(size, 8, 36);
        result.HighlightWords = Get("HighlightWords", "", 512);
        result.WholeWords = Get("WholeWords", "false") == "true";
        var background = Get("Background", "Solid");
        result.Background = background is "Solid" or "Gradient" or "Image" ? background : "Solid";
        result.Image = Get("Image", "", 350000);
        return result;
    }
}
