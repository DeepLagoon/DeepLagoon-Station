namespace Content.Server._DeepLagoon.Power;

/// <summary>
/// Allows only the listed map-authored RTGs and solar panels to supply power on this grid.
/// </summary>
[RegisterComponent]
public sealed partial class PoiPowerRestrictionComponent : Component
{
    [DataField]
    public HashSet<EntityUid> NativeSources = [];
}
