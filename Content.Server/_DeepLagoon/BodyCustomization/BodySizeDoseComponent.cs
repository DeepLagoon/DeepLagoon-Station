namespace Content.Server._DeepLagoon.BodyCustomization;

/// <summary>Unfinished doses are runtime state, never character preferences.</summary>
[RegisterComponent]
public sealed partial class BodySizeDoseComponent : Component
{
    // Signed FixedPoint2 raw units; fractions must not accumulate floating-point error.
    public readonly Dictionary<string, long> Doses = new();
}
