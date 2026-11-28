using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.BodyInsurance;

[RegisterComponent]
public sealed partial class BodyInsuranceImplantComponent : Component
{
    [DataField]
    public long PaidPrice;

    [DataField]
    public float BloodCostFraction = 0.15f;

    [DataField]
    public float SyntheticDamageFraction = 0.15f;

    [DataField]
    public float BiteBleeding = 1.5f;

    [DataField]
    public float ReturnDelay = 600f;

    [DataField]
    public float ReturnDuration = 30f;

    [DataField]
    public float PlayerBlockingRadius = 30f;

    [DataField]
    public int MinReturnDistance = 1000;

    [DataField]
    public int MaxReturnDistance = 3000;

    [DataField]
    public float ExpeditionRescueDamage = 200f;

    [DataField]
    public float ReturnBluntDamage = 5f;

    [DataField]
    public float ReturnPiercingDamage = 5f;

    [DataField]
    public float ReturnSlashDamage = 5f;
}
