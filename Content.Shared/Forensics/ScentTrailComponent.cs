using Robust.Shared.GameStates;

namespace Content.Shared.Forensics;

/// <summary>The scent information visible to scent trackers, independently of forensic evidence.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ScentTrailComponent : Component
{
    [DataField, AutoNetworkedField] public string Scent = "";
    public TimeSpan TargetTime;
}
