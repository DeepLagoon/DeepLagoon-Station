using Content.Shared.Atmos;

namespace Content.Server._DeepLagoon.Species.Hydrakin;

[RegisterComponent]
public sealed partial class HydrakinUniformCoolingComponent : Component
{
    /// <summary>
    /// How long a full cell lasts without recharging.
    /// </summary>
    [DataField]
    public TimeSpan BatteryRuntime = TimeSpan.FromMinutes(20);

    [DataField]
    public float TargetTemperature = Atmospherics.T20C;

    /// <summary>
    /// Maximum cooling in kelvins per second.
    /// </summary>
    [DataField]
    public float CoolingRate = 6f;
}
