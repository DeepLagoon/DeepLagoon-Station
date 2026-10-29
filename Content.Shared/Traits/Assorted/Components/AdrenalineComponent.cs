// Adapted from Einstein Engines.
using Robust.Shared.GameStates;

namespace Content.Shared.Traits.Assorted.Components;

/// <summary>
///     This is used for any trait that modifies the Melee System implementation of Health Contest
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AdrenalineComponent : Component
{
    /// <summary>
    ///     When true, multiplies by the inverse of the resulting Contest.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Inverse { get; set; } = false;

    /// <summary>
    ///     Used as the RangeModifier input for a Health Contest.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float RangeModifier { get; set; } = 1;

    /// <summary>
    ///     Used as the BypassClamp input for a Health Contest.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool BypassClamp { get; set; } = false;
}