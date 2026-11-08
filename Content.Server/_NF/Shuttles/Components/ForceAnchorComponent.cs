using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._NF.Shuttles.Components;

/// <summary>
/// When on a grid, sets the grid to anchored and prevents further changes on init.
/// </summary>
[RegisterComponent]
public sealed partial class ForceAnchorComponent : Component
{
    [DataField] public BodyType? PreviousBodyType;
    [DataField] public BodyStatus PreviousBodyStatus;
    [DataField] public bool PreviousFixedRotation;
    [DataField] public bool PreserveAnchorLock;
}
