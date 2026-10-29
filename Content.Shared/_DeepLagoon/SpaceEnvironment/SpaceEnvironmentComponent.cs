using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.SpaceEnvironment;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class SpaceEnvironmentComponent : Component
{
    [DataField] public string Campaign = "";
    [DataField, AutoNetworkedField] public SectorDefinition Definition = new();
    [DataField, AutoNetworkedField] public List<string> Surveyed = new();
}

[RegisterComponent, NetworkedComponent]
public sealed partial class SectorLandmarkComponent : Component;

[ByRefEvent]
public readonly record struct StarSystemAssignedEvent(EntityUid Map, string System);
