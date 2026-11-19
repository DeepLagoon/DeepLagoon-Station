using Robust.Shared.GameStates;

namespace Content.Shared.Cuffs.Components;

/// <summary>A multiplier on the wearer's time to apply restraints.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CuffingTimeModifierComponent : Component
{
    [DataField, AutoNetworkedField] public float Multiplier = 0.75f;
}
