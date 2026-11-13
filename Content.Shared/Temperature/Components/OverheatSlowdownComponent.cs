using Robust.Shared.GameStates;

namespace Content.Shared.Temperature.Components;

/// <summary>
/// Replicates the active overheat penalty for movement prediction.
/// </summary>
[RegisterComponent, NetworkedComponent, UnsavedComponent]
public sealed partial class OverheatSlowdownComponent : Component;
