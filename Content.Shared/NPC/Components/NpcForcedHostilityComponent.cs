namespace Content.Shared.NPC.Components;

/// <summary>
/// Forces hostility towards entities outside this group and player minds, regardless of factions or personal exceptions.
/// Assigned by the server to expedition defenders; visiting crew and their pets never receive it.
/// </summary>
[RegisterComponent]
public sealed partial class NpcForcedHostilityComponent : Component
{
    [DataField]
    public EntityUid Group;
}
