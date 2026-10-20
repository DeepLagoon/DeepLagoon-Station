using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Admin;

/// <summary>
/// An inert diagnostic marker for an entity omitted from an uploaded map.
/// Retaining its transform/UID preserves known children and serialized references.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AdminMapMissingPrototypeComponent : Component
{
    [DataField("prototype"), AutoNetworkedField]
    public string Prototype = string.Empty;
}
