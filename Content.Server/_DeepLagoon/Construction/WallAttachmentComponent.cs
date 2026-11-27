namespace Content.Server._DeepLagoon.Construction;

/// <summary>Removable equipment mounted on a supporting wall in the same tile.</summary>
[RegisterComponent]
public sealed partial class WallAttachmentComponent : Component
{
    [DataField]
    public bool ProtectWall;

    [DataField]
    public float ToolDelay = 1f;

    [ViewVariables]
    public EntityUid? Support;
}

/// <summary>Runtime index; rebuilt from attachments after loading a map or parking snapshot.</summary>
[RegisterComponent]
public sealed partial class WallAttachmentSupportComponent : Component
{
    public readonly HashSet<EntityUid> Attachments = new();
}
