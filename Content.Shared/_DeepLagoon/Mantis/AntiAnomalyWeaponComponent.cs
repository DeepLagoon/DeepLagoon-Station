using Content.Shared.Damage;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Mantis;

[RegisterComponent, NetworkedComponent]
public sealed partial class AntiAnomalyWeaponComponent : Component
{

    [DataField(required: true)]
    public DamageModifierSet Modifiers = default!;

    [DataField]
    public string VeilFaction = "GlimmerMonster";
}
