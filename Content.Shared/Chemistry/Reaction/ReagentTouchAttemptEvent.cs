namespace Content.Shared.Chemistry.Reaction;

/// <summary>
/// Raised before applying a reagent's contact effects to an entity.
/// </summary>
[ByRefEvent]
public struct ReagentTouchAttemptEvent
{
    public bool Cancelled;
}
