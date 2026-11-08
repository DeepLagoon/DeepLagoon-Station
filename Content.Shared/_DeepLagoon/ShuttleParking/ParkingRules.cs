using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.ShuttleParking;

public static class ParkingRules
{
    public const string StorageDescription = "Сохранённые на долгосрочном хранилище шаттлы доступны после повторного возвращения в сектор и начала новой смены. Выкуп - в 2,5 раза от исходной цены шаттла в кредитах.";
    public static string CategoryName(int category) => category switch
    {
        0 => "Маленький", 1 => "Средний", 2 => "Большой", 3 => "Сверхмассивный", 4 => "Капитальный", _ => "Недопустимый размер",
    };
    public static int SlotPrice(int category, bool additional) => (category, additional) switch
    {
        (0, false) => 65, (0, true) => 33, (1, false) => 125, (1, true) => 63,
        (2, false) => 250, (2, true) => 125, (3, false) => 550, (3, true) => 275,
        (4, false) => 1000, (4, true) => 500, _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
    // Both tile count and dimensions matter: long sparse hulls cannot use a small berth.
    public static int Category(int tiles, float width, float height)
    {
        if (tiles <= 0 || !float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(tiles));
        var length = Math.Max(width, height);
        if (tiles <= 200 && length <= 24) return 0;
        if (tiles <= 450 && length <= 32) return 1;
        if (tiles <= 1500 && length <= 64) return 2;
        if (tiles <= 5000 && length <= 100) return 3;
        return 4;
    }
    public static int RedemptionPrice(int originalPrice) => originalPrice > 0
        ? checked((int)(((long) originalPrice * 5 + 1) / 2)) : throw new ArgumentOutOfRangeException(nameof(originalPrice));
}

[Serializable, NetSerializable]
public enum ParkingUiKey : byte { Key }

[Serializable, NetSerializable]
public sealed class ParkingAction(string action, Guid slot = default, int category = 0, Guid token = default, NetEntity? dock = null) : BoundUserInterfaceMessage
{
    public readonly string Action = action;
    public readonly Guid Slot = slot;
    public readonly int Category = category;
    public readonly Guid Token = token;
    public readonly NetEntity? Dock = dock;
}

// Personal data is sent to the requesting actor, never broadcast to all console viewers.
[Serializable, NetSerializable]
public sealed class ParkingUpdate : BoundUserInterfaceMessage
{
    public bool IdPresent, Parked, HasShip, Busy;
    public string User = "", ShipName = "", Message = "", OwnerName = "";
    public int Category = -1, Credits;
    public long Coins = -1;
    public Guid ReviewToken;
    public Guid ReviewSlot;
    public Guid BuyToken;
    public List<string> Excluded = [];
    public List<ParkingPlace> Places = [];
    public List<ParkingDock> Docks = [];
    public ParkingRecallMap? RecallMap;
}

[Serializable, NetSerializable]
public sealed record ParkingPlace(Guid Id, int Category, string ShipName, string Owner, int ShipCategory, int Price, bool Recovering, bool Paid);

[Serializable, NetSerializable]
public sealed record ParkingDock(NetEntity Entity, string Name);

[Serializable, NetSerializable]
public sealed record ParkingRecallMap(List<Vector2[]> StationHull, List<Vector2[]> ShuttleHull, List<ParkingRecallPort> Ports);

[Serializable, NetSerializable]
public sealed record ParkingRecallPort(NetEntity Entity, string Name, Vector2 Position, ParkingPortStatus Status,
    Vector2? ShipOrigin, Angle ShipAngle);

[Serializable, NetSerializable]
public enum ParkingPortStatus : byte { Fits, Blocked, Occupied }
