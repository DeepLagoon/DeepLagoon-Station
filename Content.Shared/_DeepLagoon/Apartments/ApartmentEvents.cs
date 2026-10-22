using Robust.Shared.Serialization;
using Content.Shared._DeepLagoon.Mapping;

namespace Content.Shared._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentGridComponent : Component;

[RegisterComponent]
public sealed partial class ApartmentTerminalComponent : Component;

[RegisterComponent]
public sealed partial class ApartmentResidentComponent : Component;

[Serializable, NetSerializable]
public sealed class ApartmentMapFurniture
{
    public ApartmentPlacement Baseline = new();
    public string Prototype = "";
    public string Name = "";
    public bool BlocksMovement;
    public bool AllowsOverlap;
    public bool CanRotate = true;

    public ApartmentFurniturePrototype Definition() => new()
    {
        ID = Baseline.Furniture, Name = Name, Entity = Prototype, MaxCount = 1,
        BlocksMovement = BlocksMovement, ExistingOnly = true, AllowsOverlap = AllowsOverlap, CanRotate = CanRotate
    };
}

[Serializable, NetSerializable]
public sealed class ApartmentRoomScene
{
    public int X, Y, Width, Height;
    public readonly List<GridEditorItem> Tiles = new();
    public readonly List<GridEditorItem> Objects = new();
    public readonly HashSet<Vector2i> Occupied = new();
    public readonly HashSet<Vector2i> Blocked = new();
}

[Serializable, NetSerializable]
public sealed class ApartmentEditorEvent(NetEntity grid, string template, int revision, Guid token,
    List<ApartmentPlacement> layout, Dictionary<string, NetEntity> entities, ApartmentRoomScene? scene = null, int number = 0,
    List<ApartmentMapFurniture>? mapFurniture = null) : EntityEventArgs
{
    public readonly NetEntity Grid = grid;
    public readonly string Template = template;
    public readonly int Revision = revision;
    public readonly Guid Token = token;
    public readonly List<ApartmentPlacement> Layout = layout;
    public readonly Dictionary<string, NetEntity> Entities = entities;
    public readonly ApartmentRoomScene? Scene = scene;
    public readonly int Number = number;
    public readonly List<ApartmentMapFurniture> MapFurniture = mapFurniture ?? new();
}

[Serializable, NetSerializable]
public sealed class ApartmentApplyEvent(Guid token, int revision, List<ApartmentPlacement> layout) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly int Revision = revision;
    public readonly List<ApartmentPlacement> Layout = layout;
}

[Serializable, NetSerializable]
public sealed class ApartmentActionEvent(string action) : EntityEventArgs
{
    public readonly string Action = action;
}

[Serializable, NetSerializable]
public sealed class ApartmentResultEvent(bool success, string message, int revision) : EntityEventArgs
{
    public readonly bool Success = success;
    public readonly string Message = message;
    public readonly int Revision = revision;
}
