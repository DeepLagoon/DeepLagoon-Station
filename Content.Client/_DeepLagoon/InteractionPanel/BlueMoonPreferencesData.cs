using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.InteractionPanel;

namespace Content.Client._DeepLagoon.InteractionPanel;

public static class BlueMoonPreferencesData
{
    private static TguiData Number(string id, float value, float min, float max) => new TguiData()
        .String("id", id).String("label", Loc.GetString("dl-arousal-pref-" + id)).String("kind", "number")
        .Number("value", value).Number("min", min).Number("max", max);
    private static TguiData Toggle(string id, bool value) => Number(id, value ? 1 : 0, 0, 1).String("kind", "toggle");
    private static TguiData Sensitivity(string id, BlueMoonSensitivity value) => Number(id, (int)value, 0, 5)
        .String("kind", "select").Array("options", Enum.GetValues<BlueMoonSensitivity>().Select(v =>
            new TguiData().Number("id", (int)v).String("name", Loc.GetString("dl-arousal-sensitivity-" + v.ToString().ToLowerInvariant()))));

    public static TguiData[] Options(BlueMoonPreferences preferences)
    {
        var p = preferences.Sanitize();
        return [Toggle("enabled", p.Enabled), Number("tolerance", p.Tolerance, 25, 200), Number("potency", p.Potency, -1, 25),
            Toggle("aphrodisiacs", p.Aphrodisiacs), Toggle("hormones", p.HormoneChanges), Toggle("receive-fluids", p.ReceiveFluids),
            Toggle("fluid-growth", p.FluidGrowth), Toggle("pregnancy", p.Pregnancy),
            Toggle("messy", p.Messy), Toggle("estrous", p.EstrousCycle),
            Number("fertility", p.Fertility, 0, 100), Number("virility", p.Virility, 0, 100),
            Number("synthetic-volume", p.SyntheticVolume, 5, 300),
            Number("fluid-rate", p.FluidRate, 0, 10), Number("fluid-multiplier", p.FluidMultiplier, 0, 10),
            Number("fluid-maximum", p.FluidMaximum, 0, 300),
            Number("milk-rate", p.MilkRate, 0, 10), Number("milk-multiplier", p.MilkMultiplier, 0, 10), Number("milk-maximum", p.MilkMaximum, 0, 300),
            Number("female-rate", p.FemaleRate, 0, 10), Number("female-multiplier", p.FemaleMultiplier, 0, 10), Number("female-maximum", p.FemaleMaximum, 0, 300),
            Number("fluid", p.GenitalFluid switch { "DLFemaleEjaculate" => 1, "DLSynthLubricant" => 2, "Milk" => 3, _ => 0 }, 0, 3)
                .String("kind", "select").Array("options", new[] { "DLSemen", "DLFemaleEjaculate", "DLSynthLubricant", "Milk" }
                    .Select((id, index) => new TguiData().Number("id", index).String("name", Loc.GetString("reagent-name-" + (id == "Milk" ? "milk" : "dl-" + id[2..].ToLowerInvariant()))))),
            Sensitivity("penis", p.Penis), Sensitivity("vagina", p.Vagina), Sensitivity("breasts", p.Breasts), Sensitivity("anus", p.Anus)];
    }
}
