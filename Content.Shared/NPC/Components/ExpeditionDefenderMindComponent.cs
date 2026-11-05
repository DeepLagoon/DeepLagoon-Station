namespace Content.Shared.NPC.Components;

/// <summary>
/// Records the expedition where this mind took a native defender ghost role.
/// </summary>
[RegisterComponent]
public sealed partial class ExpeditionDefenderMindComponent : Component
{
    [DataField]
    public EntityUid Group;
}
