namespace Content.Server.Cargo.Components;

/// <summary>
/// Records the resale limit of an item that was produced in game. Stacks use a per-unit limit.
/// </summary>
[RegisterComponent]
public sealed partial class CraftedPriceComponent : Component
{
    [DataField]
    public double UnitLimit = 500;
}
