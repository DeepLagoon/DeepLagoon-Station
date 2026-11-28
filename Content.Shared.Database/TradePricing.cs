using System;

namespace Content.Shared.Database;

public static class TradePricing
{
    // Cumulative rounding conserves the seller's exact whole-lot price, even after many partial purchases.
    public static long Cost(long originalPrice, int originalUnits, int remainingUnits, int units)
    {
        if (originalPrice is < 1 or > 1000000000 || originalUnits is < 1 or > 1000000 ||
            remainingUnits < 1 || remainingUnits > originalUnits || units < 1 || units > remainingUnits)
            throw new ArgumentException("invalid_basket");
        var sold = originalUnits - remainingUnits;
        return (originalPrice * (sold + units) + originalUnits - 1) / originalUnits -
               (originalPrice * sold + originalUnits - 1) / originalUnits;
    }
}
