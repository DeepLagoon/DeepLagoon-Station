using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.Armor;

/// <summary>A damage source can use a different armour category without changing its injury type.</summary>
[RegisterComponent]
public sealed partial class DamageArmorOverrideComponent : Component
{
    [DataField] public bool IgnoreArmor;
    /// <summary>WhiteMoon subtracts percentage points of protection rather than exponentiating coefficients.</summary>
    [DataField] public bool SourceArmorPenetration;
    [DataField] public ProtoId<DamageTypePrototype>? ArmorType;
}
