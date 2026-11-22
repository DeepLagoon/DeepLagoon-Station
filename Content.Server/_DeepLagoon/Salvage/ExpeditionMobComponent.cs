namespace Content.Server._DeepLagoon.Salvage;

/// <summary>A mob born on an expedition must not leave its original map intact.</summary>
[RegisterComponent]
public sealed partial class ExpeditionMobComponent : Component
{
    [DataField]
    public EntityUid Expedition;
}
