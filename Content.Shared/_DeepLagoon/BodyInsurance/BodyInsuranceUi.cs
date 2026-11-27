using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.BodyInsurance;

[Serializable, NetSerializable]
public enum BodyInsuranceUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class BodyInsuranceUiState : BoundUserInterfaceState
{
    public readonly long Price;
    public readonly bool CanPurchase;
    public readonly bool Insured;

    public readonly int SubscriptionPrice;
    public readonly bool HasSubscription;
    public readonly int PurchaseCount;

    public BodyInsuranceUiState(
        long price,
        bool canPurchase,
        bool insured,
        int subscriptionPrice,
        bool hasSubscription,
        int purchaseCount)
    {
        Price = price;
        CanPurchase = canPurchase;
        Insured = insured;

        SubscriptionPrice = subscriptionPrice;
        HasSubscription = hasSubscription;
        PurchaseCount = purchaseCount;
    }
}

[Serializable, NetSerializable]
public sealed class BodyInsurancePurchaseMessage
    : BoundUserInterfaceMessage
{
}
