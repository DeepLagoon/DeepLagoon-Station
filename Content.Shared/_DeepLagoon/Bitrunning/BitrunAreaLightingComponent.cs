using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Bitrunning;

/// <summary>WhiteMoon area base lighting on this grid. Unlisted cells have no ambient light.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BitrunAreaLightingComponent : Component
{
    [DataField, AutoNetworkedField]
    public Dictionary<Vector2i, Color> Tiles = new();
}
