using Content.Shared.Shuttles.UI.MapObjects;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

[Serializable, NetSerializable]
public sealed class ShuttleBoundUserInterfaceState : BoundUserInterfaceState
{
    public Content.Shared.Exodus.ShipShields.ShipShieldState? ShieldState;
    public NavInterfaceState NavState;
    public ShuttleMapInterfaceState MapState;
    public DockingInterfaceState DockState;
    public Content.Shared._Mono.FireControl.FireControlConsoleBoundInterfaceState? FireControlState;

    public ShuttleBoundUserInterfaceState(NavInterfaceState navState, ShuttleMapInterfaceState mapState, DockingInterfaceState dockState)
    {
        NavState = navState;
        MapState = mapState;
        DockState = dockState;
    }
}
