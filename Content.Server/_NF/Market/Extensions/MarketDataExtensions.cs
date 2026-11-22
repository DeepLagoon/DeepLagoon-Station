using System.Linq;

namespace Content.Server._NF.Market.Extensions;

public static class MarketDataExtensions
{
    public static void Restock(this List<MarketLot> stock, MarketLot lot)
    {
        if (lot.Quantity <= 0)
            return;

        var existing = stock.FirstOrDefault(item =>
            item.Prototype == lot.Prototype && item.CraftedUnitLimit == lot.CraftedUnitLimit);
        if (existing != null)
            existing.Quantity += lot.Quantity;
        else
            stock.Add(lot);
    }

    public static long GetMarketValue(IEnumerable<MarketLot> lots, float marketModifier)
    {
        return lots.Sum(lot => checked((long)Math.Round(lot.Price * lot.Quantity * marketModifier)));
    }
}
