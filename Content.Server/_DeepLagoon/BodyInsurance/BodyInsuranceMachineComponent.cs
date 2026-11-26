using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server._DeepLagoon.BodyInsurance;

[RegisterComponent]
public sealed partial class BodyInsuranceMachineComponent : Component
{


    [DataField]
    public float ReturnBluntDamage = 5f;

    [DataField]
    public float ReturnPiercingDamage = 5f;

    [DataField]
    public float ReturnSlashDamage = 5f;

    [DataField]
    public float ReturnDuration = 30f;

    [DataField]
    public float BiteBleeding = 1.5f;

    [DataField]
    public int BasePrice = 100000;

    [DataField]
    public float PurchasePriceMultiplier = 1.25f;

    [DataField]
    public int SubscriptionCost = 15;

    [DataField]
    public float BloodCostFraction = 0.15f;

    [DataField]
    public float SyntheticDamageFraction = 0.15f;

    [DataField]
    public float ReturnDelay = 600f;

    [DataField]
    public float PlayerBlockingRadius = 30f;

    [DataField]
    public int MinReturnDistance = 1000;

    [DataField]
    public int MaxReturnDistance = 3000;

    [DataField]
    public float ExpeditionRescueDamage = 200f;
}
