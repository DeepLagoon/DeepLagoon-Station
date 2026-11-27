using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Mantis;

[RegisterComponent, NetworkedComponent]
public sealed partial class InsulativeShieldComponent : Component
{

    [DataField]
    public bool Passthrough;

    [ViewVariables]
    public bool Active;
}
