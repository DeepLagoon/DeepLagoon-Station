using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Salvage;

/// <summary>
/// The expedition's ground grid. Floor coverings may be removed, but the ground beneath them may not.
/// This belongs to the expedition grid itself, never to visiting shuttles.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ExpeditionGroundComponent : Component;
