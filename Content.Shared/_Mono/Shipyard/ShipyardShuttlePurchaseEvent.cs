namespace Content.Shared._Mono.Shipyard;

public sealed class ShipyardShuttlePurchaseEvent(EntityUid shuttle, EntityUid purchaser, bool purchasedWithVoucher = false)
{
    public EntityUid Shuttle { get;  } = shuttle;
    public EntityUid Purchaser { get; } = purchaser;
    public bool PurchasedWithVoucher { get; } = purchasedWithVoucher;
}
