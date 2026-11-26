using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.BodyInsurance;

[RegisterComponent]
public sealed partial class BodyInsuranceComponent : Component
{

    [DataField]
    public int PurchaseCount;

    [DataField]
    public EntityUid? InsuranceImplant;

    // Фактическая стоимость последней страховки.
    [DataField]
    public long PaidInsurancePrice;

    // Награда за обнаружение тела уже выплачена.
    [DataField]
    public bool RescueDiscoveryPaid;

    // Оставшаяся награда за реанимацию.
    [DataField]
    public long RescueRevivalReward;

    // Аккаунт владельца страховки.
    [DataField]
    public NetUserId? InsuranceAccount;

    [DataField]
    public float ReturnBluntDamage = 5f;

    [DataField]
    public float ReturnPiercingDamage = 5f;

    [DataField]
    public float ReturnSlashDamage = 5f;

    [DataField]
    public float ReturnDuration = 30f;

    [DataField]
    public bool Insured;

    [DataField]
    public int DeathCount;

    [DataField]
    public TimeSpan? DeathTime;

    [DataField]
    public EntityUid? ReturnAction;

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
    public TimeSpan NextReturnCheck;
}
