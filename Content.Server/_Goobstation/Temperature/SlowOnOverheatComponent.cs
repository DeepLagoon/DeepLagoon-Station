using Content.Shared.Atmos;

namespace Content.Server._Goobstation.Temperature;

/// <summary>
/// Slows an entity while its temperature is at or above a threshold.
/// </summary>
// Keep the serialized name for existing prototypes and saved entities.
[RegisterComponent, ComponentProtoName("KillOnOverheat"), Access(typeof(SlowOnOverheatSystem))]
public sealed partial class SlowOnOverheatComponent : Component
{
    [DataField]
    public float OverheatThreshold = Atmospherics.T0C + 110f;

    [DataField]
    public LocId OverheatPopup = "ipc-overheat-popup";
}
