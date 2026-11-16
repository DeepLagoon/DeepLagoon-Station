using Robust.Shared.GameStates;

namespace Content.Shared.Shuttles.Components;

/// <summary>
/// The shuttle's current flight input comes entirely from its console autopilot.
/// </summary>
[RegisterComponent, NetworkedComponent, UnsavedComponent]
public sealed partial class ShuttleAutopilotActiveComponent : Component;
