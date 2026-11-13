namespace Content.Shared.Movement.Components;

/// <summary>Server-side selection of a jetpack, including when its thrust is paused.</summary>
[RegisterComponent]
public sealed partial class JetpackSelectionComponent : Component
{
    public EntityUid Pack;
}
