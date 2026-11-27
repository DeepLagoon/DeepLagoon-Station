using Robust.Shared.GameStates;

namespace Content.Shared._Mono.ShipGuns;

/// <summary>Ship fire-control weapons must not impart recoil impulses on their mounting or hull.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShipGunNoRecoilComponent : Component;
