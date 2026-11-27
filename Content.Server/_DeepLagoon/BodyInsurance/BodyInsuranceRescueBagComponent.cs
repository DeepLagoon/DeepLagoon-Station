using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.BodyInsurance;

[RegisterComponent]
public sealed partial class BodyInsuranceRescueBagComponent : Component
{
    [DataField]
    public EntityUid? Body;
}
