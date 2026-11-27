using Robust.Shared.Serialization;

namespace Content.Shared.Cargo.BUI;

[NetSerializable, Serializable]
public sealed class CargoConsoleInterfaceState : BoundUserInterfaceState
{
    public Dictionary<string, int> Prices = new();
    public string Name;
    public int Count;
    public int Capacity;
    public long Balance;
    public List<CargoOrderData> Orders;

    public CargoConsoleInterfaceState(string name, int count, int capacity, long balance, List<CargoOrderData> orders)
    {
        Name = name;
        Count = count;
        Capacity = capacity;
        Balance = balance;
        Orders = orders;
    }
}