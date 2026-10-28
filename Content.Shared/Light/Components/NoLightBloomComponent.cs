using Robust.Shared.GameStates;

namespace Content.Shared.Light.Components;

/// <summary>Preserves the source's normal lighting while excluding its decorative bloom.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NoLightBloomComponent : Component;
