using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Mapping;

/// <summary>Grid-local coordinates; entity centers and decal origins retain sub-tile precision.</summary>
[Serializable, NetSerializable]
public sealed class GridEditorItem
{
    public string Id = "";
    public string Kind = "";
    public string Prototype = "";
    public float X;
    public float Y;
    public float Rotation;
    public string Color = "#FFFFFFFF";
    public int Z;
    public bool Cleanable;
    public bool Anchored;
    public byte Variant;
    public byte Flags;
    public byte Mirror;
    public bool Same(GridEditorItem other) => Id == other.Id && Kind == other.Kind && Prototype == other.Prototype &&
        X == other.X && Y == other.Y && Rotation == other.Rotation && Color == other.Color && Z == other.Z &&
        Cleanable == other.Cleanable && Anchored == other.Anchored && Variant == other.Variant && Flags == other.Flags && Mirror == other.Mirror;
}

[Serializable, NetSerializable]
public sealed class GridEditorStateEvent(NetEntity grid, Guid token, int x, int y, int width, int height,
    List<GridEditorItem> items, bool initialized = false, int gridX = 0, int gridY = 0, int gridWidth = 0, int gridHeight = 0) : EntityEventArgs
{
    public readonly NetEntity Grid = grid;
    public readonly Guid Token = token;
    public readonly int X = x, Y = y, Width = width, Height = height;
    public readonly List<GridEditorItem> Items = items;
    public readonly bool Initialized = initialized;
    public readonly int GridX = gridX, GridY = gridY, GridWidth = gridWidth, GridHeight = gridHeight;
}

[Serializable, NetSerializable]
public sealed class GridEditorExpansion
{
    public int Left, Right, Bottom, Top;
}

[Serializable, NetSerializable]
public sealed class GridEditorApplyEvent(Guid token, List<GridEditorItem> items, GridEditorExpansion? expansion = null, bool patch = false) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly List<GridEditorItem> Items = items;
    public readonly GridEditorExpansion? Expansion = expansion;
    public readonly bool Patch = patch;
}

[Serializable, NetSerializable]
public sealed class GridEditorResultEvent(string message, bool success) : EntityEventArgs
{
    public readonly string Message = message;
    public readonly bool Success = success;
}

[Serializable, NetSerializable]
public sealed class GridEditorCloseEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class GridEditorRegionEvent(Guid token, int x, int y) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly int X = x, Y = y;
}

[Serializable, NetSerializable]
public sealed class GridEditorDownloadEvent(Guid token, Guid request, bool wholeMap) : EntityEventArgs
{
    public readonly Guid Token = token, Request = request;
    public readonly bool WholeMap = wholeMap;
}

[Serializable, NetSerializable]
public sealed class GridEditorFileEvent(Guid request, int index, int count, byte[] bytes, string error = "") : EntityEventArgs
{
    public readonly Guid Request = request;
    public readonly int Index = index, Count = count;
    public readonly byte[] Bytes = bytes;
    public readonly string Error = error;
}

/// <summary>Only authoritative items changed by an apply, including assigned entity/decal IDs.</summary>
[Serializable, NetSerializable]
public sealed class GridEditorPatchEvent(Guid token, List<GridEditorItem> items, List<string> removed,
    int x, int y, int width, int height, int gridX, int gridY, int gridWidth, int gridHeight, bool applied = false, int previewGeneration = 0) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly List<GridEditorItem> Items = items;
    public readonly List<string> Removed = removed;
    public readonly bool Applied = applied;
    public readonly int PreviewGeneration = previewGeneration;
    public readonly int X = x, Y = y, Width = width, Height = height;
    public readonly int GridX = gridX, GridY = gridY, GridWidth = gridWidth, GridHeight = gridHeight;
}

[Serializable, NetSerializable]
public sealed class GridEditorDraftUpdateEvent(Guid token, int generation, List<GridEditorItem> changes, GridEditorExpansion? expansion = null) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly int Generation = generation;
    public readonly List<GridEditorItem> Changes = changes;
    public readonly GridEditorExpansion? Expansion = expansion;
}

[Serializable, NetSerializable]
public sealed class GridEditorPeerDraft
{
    public string Player = "", Name = "";
    public GridEditorExpansion Expansion = new();
    public List<GridEditorItem> Items = new(), RemovedItems = new();
}

[Serializable, NetSerializable]
public sealed class GridEditorDraftsEvent(Guid token, List<GridEditorPeerDraft> peers) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly List<GridEditorPeerDraft> Peers = peers;
}
