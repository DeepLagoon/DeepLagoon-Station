using Robust.Shared.GameStates;

namespace Content.Shared.Mood;

/// <summary>
///     Replicates the mood values needed for prediction. Individual mood effects remain on the server.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NetMoodComponent : Component
{
    [DataField, AutoNetworkedField]
    public float CurrentMoodLevel;

    [DataField, AutoNetworkedField]
    public float NeutralMoodThreshold;

    [AutoNetworkedField]
    public float SprintSpeedModifier = 1f;
}
