using Robust.Shared.Serialization;

namespace Content.Shared.Chemistry.Reagent;

/// <summary>Color travels with this donor's blood when it is drawn, spilled or mixed.</summary>
[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public sealed partial class BloodColorData : ReagentData
{
    [DataField]
    public Color Color;

    public override ReagentData Clone() => new BloodColorData { Color = Color };
    public override bool Equals(ReagentData? other) => other is BloodColorData data && data.Color == Color;
    public override int GetHashCode() => Color.GetHashCode();

    public static Color GetColor(ReagentId reagent, Color fallback)
    {
        if (reagent.Data != null)
            foreach (var data in reagent.Data)
                if (data is BloodColorData color)
                    return color.Color;
        return fallback;
    }
}
