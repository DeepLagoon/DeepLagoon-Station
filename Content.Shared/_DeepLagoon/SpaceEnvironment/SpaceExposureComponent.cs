using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SpaceExposureComponent : Component
{
    [AutoNetworkedField] public float Dust;
    [AutoNetworkedField] public float Plasma;
    [AutoNetworkedField] public float Radiation;
}

/// <summary>Optional equipment/grid integration; 0 blocks the corresponding influence, 1 is unprotected.</summary>
[RegisterComponent]
public sealed partial class SpaceEnvironmentProtectionComponent : Component
{
    [DataField] public float DustMultiplier = 1f;
    [DataField] public float PlasmaMultiplier = 1f;
    [DataField] public float RadiationMultiplier = 1f;
}

[ByRefEvent]
public record struct SpaceEnvironmentHitEvent(float Load, bool Absorbed = false);
