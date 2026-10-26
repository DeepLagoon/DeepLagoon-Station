using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Apartments;

public static class ApartmentFurnitureStock
{
    public static int Capacity(ApartmentFurniturePrototype product, IEnumerable<ApartmentFurniturePrototype> variants,
        IReadOnlyDictionary<string, int> purchased, IReadOnlySet<string> legacy)
    {
        long count = product.Price == 0 ? product.MaxCount : 0;
        count += variants.Where(item => item.Price > 0 && legacy.Contains(item.ID)).Sum(item => (long) item.MaxCount);
        count += purchased.GetValueOrDefault(product.ID);
        return (int) Math.Clamp(count, 0, int.MaxValue);
    }
}

[Serializable, NetSerializable]
public sealed class ApartmentProduct
{
    public string Id = "", Name = "", Category = "";
    public long Price;
    public int PackSize, Stock;
    public string[] Variants = [];
}

[Serializable, NetSerializable]
public sealed class ApartmentCommerceRequest(Guid token) : EntityEventArgs
{
    public readonly Guid Token = token;
}

[Serializable, NetSerializable]
public sealed class ApartmentFurniturePurchaseEvent(Guid token, Guid order, string product, int quantity, long unitPrice) : EntityEventArgs
{
    public readonly Guid Token = token, Order = order;
    public readonly string Product = product;
    public readonly int Quantity = quantity;
    public readonly long UnitPrice = unitPrice;
}

[Serializable, NetSerializable]
public sealed class ApartmentCommerceStateEvent(Guid token, long balance, List<ApartmentProduct> products,
    Guid order = default, bool success = false, string message = "", bool completed = true) : EntityEventArgs
{
    public readonly Guid Token = token, Order = order;
    public readonly long Balance = balance;
    public readonly List<ApartmentProduct> Products = products;
    public readonly bool Success = success, Completed = completed;
    public readonly string Message = message;
}
