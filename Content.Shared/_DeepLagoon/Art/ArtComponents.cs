using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Art;

public static class ArtLimits
{
    public const int Resolution = 768;
    public const int ThumbnailSize = 128;
    public const int MaxLayers = 8;
    public const int MaxDocumentChars = 8 * 1024 * 1024;
    public const int ChunkChars = 5000;
    public const int MaxPreviewChars = 100000;
    public const string EmptyDocument = "{\"version\":1,\"title\":\"\",\"layers\":[{\"name\":\"Слой 1\",\"visible\":true,\"opacity\":1,\"png\":\"\",\"mask\":\"\"}]}";
}

/// <summary>Only the small world preview is replicated to observers, never the editable layers.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class ArtDisplayComponent : Component
{
    [DataField, AutoNetworkedField] public string Preview = "";
    [DataField] public Vector2 DrawingSize = new(20, 20);
}

[RegisterComponent]
public sealed partial class ArtCanvasComponent : Component
{
    [DataField] public string Document = ArtLimits.EmptyDocument;
    [DataField] public string SavedPreview = "";
    [DataField] public string DraftDocument = "";
    [DataField] public string DraftPreview = "";
    [DataField] public Guid RecoveryId;
    [DataField] public Guid RecoveryOwner;
    [DataField] public int DraftEpoch;
    [DataField] public int DraftVersion;
    [DataField] public string DraftWriter = "";
    [DataField] public int DraftSequence;
    public int DraftLeaseGeneration;
    [DataField] public Guid WorkId;
    [DataField] public Guid Author;
    [DataField] public string AuthorName = "";
    [DataField] public string Title = "";
    [DataField] public string RevisionHash = "";
    public int Revision;
    public EntityUid? Artist;
    public EntityUid? Tool;
}

[RegisterComponent]
public sealed partial class ArtToolComponent : Component
{
    [DataField] public string Kind = "brush";
    [DataField] public Color Color = Color.FromHex("#202020");
}

[RegisterComponent]
public sealed partial class ArtFrameItemComponent : Component;

[RegisterComponent]
public sealed partial class ArtFrameComponent : Component
{
    /// <summary>Explicit mapper opt-in; newly crafted frames remain empty.</summary>
    [DataField] public bool FillOnRoundStart;
    public bool Busy;
}

[Serializable, NetSerializable] public enum ArtUiKey : byte { Key }
[Serializable, NetSerializable]
public sealed class ArtEditorState(string document, int revision, string tool, string status = "", int result = 0, string color = "#202020") : BoundUserInterfaceState
{
    public string Document = document;
    public string SavedDocument = document;
    public bool HasDraft;
    public string RecoveryKey = "";
    public int DraftEpoch;
    public int DraftVersion;
    public string DraftWriter = "";
    public int DraftSequence;
    public int Revision = revision;
    public string Tool = tool;
    public string Color = color;
    public string Status = status;
    public int Result = result;
    public bool TabletAvailable;
    public bool TabletActive;
    public int TabletRevision;
}

/// <summary>Bounded content messages over the existing BUI transport.</summary>
[Serializable, NetSerializable]
public sealed class ArtEditorMessage(string action, string text, int revision, int index = 0, int count = 0, string writer = "", int sequence = 0, int epoch = 0, int version = 0) : BoundUserInterfaceMessage
{
    public string Action = action;
    public string Text = text;
    public int Revision = revision;
    public int Index = index;
    public int Count = count;
    public string Writer = writer;
    public int Sequence = sequence;
    public int Epoch = epoch;
    public int Version = version;
}

/// <summary>Delivered only to the editing actor; capability links must never be stored in replicated UI state.</summary>
[Serializable, NetSerializable]
public sealed class ArtTabletLinkMessage(string url) : BoundUserInterfaceMessage
{
    public string Url = url;
}

[Serializable, NetSerializable]
public sealed class ArtDraftStatusMessage(string writer, int sequence, int epoch, int version, bool success) : BoundUserInterfaceMessage
{
    public string Writer = writer;
    public int Sequence = sequence;
    public int Epoch = epoch;
    public int Version = version;
    public bool Success = success;
}
