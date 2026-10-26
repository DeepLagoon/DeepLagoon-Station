using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Apartments;

[CVarDefs]
public static class ApartmentCVars
{
    public static readonly CVarDef<bool> Enabled = CVarDef.Create("deeplagoon.apartments_enabled", true, CVar.SERVERONLY);
    public static readonly CVarDef<string> StopMap = CVarDef.Create("deeplagoon.apartments_stop_map",
        "/Maps/_DeepLagoon/Apartments/residential_stop.yml", CVar.SERVERONLY);
    public static readonly CVarDef<string> DefaultTemplate = CVarDef.Create("deeplagoon.apartments_default_template", "DLApartmentBasic", CVar.SERVERONLY);
    public static readonly CVarDef<int> LoadedLimit = CVarDef.Create("deeplagoon.apartments_loaded_limit", 30, CVar.SERVERONLY);
    public static readonly CVarDef<int> EmptyTimeout = CVarDef.Create("deeplagoon.apartments_empty_timeout", 120, CVar.SERVERONLY);
}

/// <summary>Furniture definition. Entitlements/payments are separate from placement and rendering.</summary>
[Prototype]
public sealed partial class ApartmentFurniturePrototype : IPrototype
{
    [IdDataField] public string ID { get; set; } = default!;
    [DataField(required: true)] public string Name = "";
    [DataField(required: true)] public EntProtoId Entity;
    [DataField] public Vector2i Size = Vector2i.One;
    [DataField] public int MaxCount = 4;
    [DataField] public string Product = "";
    public string StockKey => Product.Length == 0 ? ID : Product;
    [DataField] public long Price;
    [DataField] public string Category = "Мебель";
    [DataField] public string Layer = "Furniture";
    [DataField] public bool BlocksMovement = true;
    [DataField] public bool ExistingOnly;
    [DataField] public bool AllowsOverlap;
    [DataField] public bool CanRotate = true;
}

/// <summary>Versioned, bounded room template. Stable item IDs must survive template updates.</summary>
[Prototype]
public sealed partial class ApartmentTemplatePrototype : IPrototype
{
    [IdDataField] public string ID { get; set; } = default!;
    [DataField] public string Name = "";
    [DataField] public int Version = 1;
    [DataField] public string? Map;
    [DataField] public bool Available = true;
    [DataField] public long Price;
    [DataField] public List<Vector2i> ReservedCells = new();
    [DataField] public int Width = 10;
    [DataField] public int Height = 8;
    [DataField] public string Floor = "FloorWood";
    [DataField] public EntProtoId Wall = "WallSolid";
    [DataField] public int MaxFurniture = ApartmentLayout.SafetyLimit;
    [DataField] public int MaxVisitors = 4;
    [DataField] public Vector2i Arrival = new(1, 1);
    [DataField] public List<ProtoId<ApartmentFurniturePrototype>> Catalog = new();
    [DataField] public List<ApartmentPlacement> Furniture = new();
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class ApartmentPlacement
{
    [DataField(required: true)] public string Id { get; set; } = "";
    [DataField(required: true)] public string Furniture { get; set; } = "";
    [DataField] public int X { get; set; }
    [DataField] public int Y { get; set; }
    [DataField] public int Rotation { get; set; }

    public ApartmentPlacement Copy() => new() { Id = Id, Furniture = Furniture, X = X, Y = Y, Rotation = Rotation };
    public bool SameAs(ApartmentPlacement other) => Id == other.Id && Furniture == other.Furniture &&
        X == other.X && Y == other.Y && Rotation == other.Rotation;
}

/// <summary>A compact final-state patch, never an unbounded action log or an ECS snapshot.</summary>
public sealed class ApartmentDelta
{
    public int SchemaVersion { get; set; } = 1;
    public string Template { get; set; } = "";
    public int TemplateVersion { get; set; }
    public int Revision { get; set; }
    public List<string> Removed { get; set; } = new();
    public List<ApartmentPlacement> Changed { get; set; } = new();
    public List<ApartmentPlacement> Added { get; set; } = new();
}
