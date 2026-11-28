using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.ShuttleParking;

[Serializable, NetSerializable]
public sealed class ParkingRepairAction(string action, string[]? selected = null, Guid token = default, bool coins = false) : BoundUserInterfaceMessage
{
    public readonly string Action = action;
    public readonly string[] Selected = selected ?? [];
    public readonly Guid Token = token;
    public readonly bool Coins = coins;
}

[Serializable, NetSerializable]
public sealed record ParkingRepairPart(string Id, string Name, string Prototype, Vector2 Position, Angle Rotation,
    int Tile, string Kind, double Seconds, int Charges, NetEntity? Original = null, NetEntity? Source = null);

[Serializable, NetSerializable]
public sealed record ParkingRepairVisual(string Prototype, Vector2 Position, Angle Rotation, int Tile, byte Variant = 0);

[Serializable, NetSerializable]
public sealed class ParkingRepairView
{
    public string Ship = "", Message = "";
    public bool Parked, Working;
    public int Remaining;
    public Guid Token;
    public long Credits, Coins, Materials;
    public int Seconds;
    public string[] Selected = [];
    public List<ParkingRepairPart> Parts = [];
    public List<ParkingRepairVisual> Current = [];
}
