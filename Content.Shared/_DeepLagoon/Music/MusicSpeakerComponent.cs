using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Music;

[RegisterComponent, NetworkedComponent]
public sealed partial class MusicSpeakerComponent : Component
{
    [DataField]
    public bool AdminOnly;
}
