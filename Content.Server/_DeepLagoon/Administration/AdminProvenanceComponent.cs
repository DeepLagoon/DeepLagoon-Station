using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Administration;

/// <summary>
/// Server-only attribution for administrator-created and VV-edited entities.
/// Spawn attribution survives later edits; modification attribution records the latest edit.
/// </summary>
[RegisterComponent]
public sealed partial class AdminProvenanceComponent : Component
{
    [DataField]
    public NetUserId? SpawnedBy;

    [DataField]
    public string? SpawnedByName;

    [DataField]
    public DateTime? SpawnedAt;

    [DataField]
    public NetUserId? ModifiedBy;

    [DataField]
    public string? ModifiedByName;

    [DataField]
    public DateTime? ModifiedAt;
}
