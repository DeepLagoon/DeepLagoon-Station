using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.InteractionPanel;

[Serializable, NetSerializable]
public enum BlueMoonSensitivity : byte { Normal, Hypersensitive, Overstimulated, Disappointing, EdgingOnly, Impotent }

[Serializable, NetSerializable, DataDefinition]
public sealed partial record BlueMoonPreferences
{
    [DataField] public bool Enabled { get; set; } = true;
    [DataField] public float Tolerance { get; set; } = 100;
    [DataField] public int Potency { get; set; } = 15;
    [DataField] public bool Aphrodisiacs { get; set; } = true;
    [DataField] public bool HormoneChanges { get; set; }
    [DataField] public bool FluidGrowth { get; set; }
    [DataField] public bool ReceiveFluids { get; set; }
    [DataField] public bool Messy { get; set; }
    [DataField] public bool EstrousCycle { get; set; }
    [DataField] public bool Pregnancy { get; set; }
    [DataField] public float Fertility { get; set; }
    [DataField] public float Virility { get; set; }
    [DataField] public float SyntheticVolume { get; set; } = 25;
    [DataField] public float FluidRate { get; set; } = 2;
    [DataField] public float FluidMultiplier { get; set; } = 1;
    [DataField] public float FluidMaximum { get; set; } = 50;
    [DataField] public float MilkRate { get; set; } = 3;
    [DataField] public float MilkMultiplier { get; set; } = 1;
    [DataField] public float MilkMaximum { get; set; } = 50;
    [DataField] public float FemaleRate { get; set; } = 2;
    [DataField] public float FemaleMultiplier { get; set; } = 1;
    [DataField] public float FemaleMaximum { get; set; } = 50;
    [DataField] public string GenitalFluid { get; set; } = "DLSemen";
    [DataField] public BlueMoonSensitivity Penis { get; set; }
    [DataField] public BlueMoonSensitivity Vagina { get; set; }
    [DataField] public BlueMoonSensitivity Breasts { get; set; }
    [DataField] public BlueMoonSensitivity Anus { get; set; }

    public BlueMoonSensitivity Sensitivity(string? part) => part switch
    {
        "penis" => Penis, "vagina" => Vagina, "breasts" => Breasts, "anus" => Anus,
        _ => BlueMoonSensitivity.Normal,
    };

    public BlueMoonPreferences Sanitize() => this with
    {
        Tolerance = Clamp(Tolerance, 25, 200, 100), Potency = Math.Clamp(Potency, -1, 25),
        SyntheticVolume = Clamp(SyntheticVolume, 5, 300, 25),
        Fertility = Clamp(Fertility, 0, 100, 0), Virility = Clamp(Virility, 0, 100, 0),
        FluidRate = Clamp(FluidRate, 0, 10, 2), FluidMultiplier = Clamp(FluidMultiplier, 0, 10, 1),
        FluidMaximum = Clamp(FluidMaximum, 0, 300, 50),
        MilkRate = Clamp(MilkRate, 0, 10, 3), MilkMultiplier = Clamp(MilkMultiplier, 0, 10, 1), MilkMaximum = Clamp(MilkMaximum, 0, 300, 50),
        FemaleRate = Clamp(FemaleRate, 0, 10, 2), FemaleMultiplier = Clamp(FemaleMultiplier, 0, 10, 1), FemaleMaximum = Clamp(FemaleMaximum, 0, 300, 50),
        GenitalFluid = GenitalFluid is "DLSemen" or "DLFemaleEjaculate" or "DLSynthLubricant" or "Milk" ? GenitalFluid : "DLSemen",
        Penis = Valid(Penis), Vagina = Valid(Vagina), Breasts = Valid(Breasts), Anus = Valid(Anus),
    };

    public BlueMoonPreferences WithOption(string key, float value)
    {
        if (!float.IsFinite(value)) return this;
        return (key switch
        {
            "enabled" => this with { Enabled = value != 0 },
            "tolerance" => this with { Tolerance = value }, "potency" => this with { Potency = (int)Math.Clamp(value, -1, 25) },
            "aphrodisiacs" => this with { Aphrodisiacs = value != 0 }, "hormones" => this with { HormoneChanges = value != 0 },
            "fluid-growth" => this with { FluidGrowth = value != 0 }, "pregnancy" => this with { Pregnancy = value != 0 },
            "receive-fluids" => this with { ReceiveFluids = value != 0 },
            "messy" => this with { Messy = value != 0 }, "estrous" => this with { EstrousCycle = value != 0 },
            "fertility" => this with { Fertility = value }, "virility" => this with { Virility = value },
            "synthetic-volume" => this with { SyntheticVolume = value },
            "fluid-rate" => this with { FluidRate = value }, "fluid-multiplier" => this with { FluidMultiplier = value },
            "fluid-maximum" => this with { FluidMaximum = value },
            "milk-rate" => this with { MilkRate = value }, "milk-multiplier" => this with { MilkMultiplier = value }, "milk-maximum" => this with { MilkMaximum = value },
            "female-rate" => this with { FemaleRate = value }, "female-multiplier" => this with { FemaleMultiplier = value }, "female-maximum" => this with { FemaleMaximum = value },
            "penis" => this with { Penis = (BlueMoonSensitivity)(int)Math.Clamp(value, 0, 5) },
            "vagina" => this with { Vagina = (BlueMoonSensitivity)(int)Math.Clamp(value, 0, 5) },
            "breasts" => this with { Breasts = (BlueMoonSensitivity)(int)Math.Clamp(value, 0, 5) },
            "anus" => this with { Anus = (BlueMoonSensitivity)(int)Math.Clamp(value, 0, 5) },
            "fluid" => this with { GenitalFluid = (int)value switch { 1 => "DLFemaleEjaculate", 2 => "DLSynthLubricant", 3 => "Milk", _ => "DLSemen" } },
            _ => this,
        }).Sanitize();
    }

    private static BlueMoonSensitivity Valid(BlueMoonSensitivity value) => Enum.IsDefined(value) ? value : BlueMoonSensitivity.Normal;
    private static float Clamp(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public static class BlueMoonArousalRules
{
    public static float HeatFertilityBonus(float seconds) => 5 + Math.Clamp(seconds / 600 * 5, 0, 15);
    public static float Threshold(float tolerance) => (float.IsFinite(tolerance) ? Math.Clamp(tolerance, 25, 200) : 100) * 3;
    public static float StimulationMultiplier(BlueMoonSensitivity sensitivity) => sensitivity switch
    {
        BlueMoonSensitivity.Hypersensitive => 3, BlueMoonSensitivity.Overstimulated => 1.8f,
        BlueMoonSensitivity.Disappointing => .75f, BlueMoonSensitivity.EdgingOnly => .5f,
        BlueMoonSensitivity.Impotent => 0, _ => 1,
    };

    public static float MoanChance(float current, float maximum)
    {
        if (!float.IsFinite(current) || !float.IsFinite(maximum) || maximum <= 0 || current < maximum / 2) return 0;
        var t = (current - maximum / 2) / (maximum / 2);
        return Math.Clamp(MathF.Round(2 * (1 - t) * t * 13.8f + t * t * 100), 0, 100);
    }

    public static float FluidAmount(float seconds, float rate, float multiplier, float maximum) =>
        float.IsFinite(seconds) && float.IsFinite(rate) && float.IsFinite(multiplier) && float.IsFinite(maximum)
            ? Math.Clamp(Math.Max(0, seconds) / 10 * Math.Max(0, rate) * Math.Max(0, multiplier), 0, Math.Clamp(maximum, 0, 300)) : 0;

    public static float RecoverySeconds(int potency, int orgasms, float randomSeconds, bool extendedBranch = false) =>
        Math.Max(0, Math.Clamp(randomSeconds, 30, 90) - (potency == -1 || extendedBranch && orgasms > potency * .34f ? 0 : potency / 10f));

    public static float FertilizationChance(float virility, float fertility, float variation) =>
        Math.Clamp(Math.Min(virility * .8f, fertility * 1.2f) * (1 + Math.Clamp(variation, -.15f, .15f)), 0, 100);
}
