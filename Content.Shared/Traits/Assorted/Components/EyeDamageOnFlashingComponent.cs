// Adapted from Einstein Engines.
using Robust.Shared.GameStates;

namespace Content.Shared.Traits.Assorted.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class EyeDamageOnFlashingComponent : Component
{
    /// <summary>
    ///   How much to modify the duration of flashes against this entity.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float FlashDurationMultiplier = 1.5f;

    /// <summary>
    ///   Chance to get EyeDamage on flash
    /// </summary>
    [DataField, AutoNetworkedField]
    public float EyeDamageChance = 0.3f;

    /// <summary>
    ///   How many EyeDamage when flashed? (If EyeDamageChance check passed)
    /// </summary>
    [DataField, AutoNetworkedField]
    public int EyeDamage = 1;
}
