namespace Content.Shared._DeepLagoon.Mapping;

[RegisterComponent]
public sealed partial class GridEnvironmentComponent : Component
{
    [DataField] public bool AlwaysPowered;
    [DataField] public bool ForceGravity;
    [DataField] public bool SafeAtmosphere;
    [DataField] public bool GravityCaptured;
    [DataField] public bool OriginalGravityEnabled;
    [DataField] public bool OriginalGravityInherent;
}

[RegisterComponent]
public sealed partial class GridPowerOverrideComponent : Component
{
    [DataField] public bool OriginalNeedsPower;
    [DataField] public bool OriginalPowerDisabled;
}

[RegisterComponent]
public sealed partial class GridConsumerOverrideComponent : Component
{
    [DataField] public bool OriginalBypass;
    [DataField] public bool OriginalNetworkEnabled;
}
