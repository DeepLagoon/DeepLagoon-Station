namespace Content.Server.Traits;

[RegisterComponent]
public sealed partial class BloodDeficiencyComponent : Component
{
    [DataField] public float BloodLossPercentage = 0.0002333333f;
}
