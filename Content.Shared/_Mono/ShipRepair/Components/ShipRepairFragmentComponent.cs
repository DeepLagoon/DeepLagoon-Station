using Robust.Shared.GameStates;

namespace Content.Shared._Mono.ShipRepair.Components;

/// <summary>
/// Identifies the repair snapshots whose grids this fragment split from during the round.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ShipRepairFragmentComponent : Component
{
    [AutoNetworkedField]
    public HashSet<NetEntity> SourceGrids = new();
}
