namespace Content.Server._DeepLagoon.Economy;

/// <summary>Actual sunk fiat paid for a capital asset. Saved with it; resale returns principal, not newly appraised loot.</summary>
[RegisterComponent]
public sealed partial class PurchasedCapitalComponent : Component
{
    [DataField] public int Paid;
}
