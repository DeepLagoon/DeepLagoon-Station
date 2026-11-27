namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    /// <summary>Uses the ordinary shipyard valuation and sale multiplier, spread across simulation ticks.</summary>
    public IEnumerable<bool> AppraiseParkedShuttleSteps(EntityUid grid, Action<int> completed)
    {
        return _pricing.AppraiseGridSteps(grid,
            price => completed((int) Math.Clamp(price * _baseSaleRate, 0, int.MaxValue)));
    }
}
