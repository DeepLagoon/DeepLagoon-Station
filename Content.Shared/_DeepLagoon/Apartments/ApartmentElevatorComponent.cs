using System.Numerics;
using Robust.Shared.Audio;
using Robust.Shared.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared._DeepLagoon.Apartments;

[RegisterComponent]
public sealed partial class ApartmentElevatorComponent : Component
{
    // Grid-local offsets from the panel. Bounds include passenger tile centers.
    [DataField] public Vector2 CabinMin = new(-1, -1);
    [DataField] public Vector2 CabinMax = new(1, 1);
    [DataField] public List<Vector2> DoorOffsets = new() { new(0, -2) };
    [DataField] public float TravelTime = 12;
    [DataField] public SoundSpecifier? HissSound;
    [DataField] public SoundSpecifier? TravelSound;
    [DataField] public SoundSpecifier? ArrivalSound;
    [DataField] public SoundSpecifier? AnnouncementSound;
}

[RegisterComponent, NetworkedComponent]
public sealed partial class ApartmentElevatorDoorComponent : Component;

[Serializable, NetSerializable]
public sealed class ApartmentElevatorStateEvent(Guid token, int number, bool atHome, bool busy, string message) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly int Number = number;
    public readonly bool AtHome = atHome, Busy = busy;
    public readonly string Message = message;
}

[Serializable, NetSerializable]
public sealed class ApartmentElevatorClosedEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class ApartmentElevatorActionEvent(Guid token, string action, int number = 0) : EntityEventArgs
{
    public readonly Guid Token = token;
    public readonly string Action = action;
    public readonly int Number = number;
}

public sealed class SharedApartmentElevatorDoorSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ApartmentElevatorDoorComponent, Content.Shared.Doors.BeforeDoorOpenedEvent>((uid, comp, args) => args.Cancel());
        SubscribeLocalEvent<ApartmentElevatorDoorComponent, Content.Shared.Prying.Components.BeforePryEvent>((EntityUid uid, ApartmentElevatorDoorComponent comp, ref Content.Shared.Prying.Components.BeforePryEvent args) => args.Cancelled = true);
    }
}

[Serializable, NetSerializable]
public sealed class ApartmentElevatorCurtainEvent(float duration) : EntityEventArgs
{
    public readonly float Duration = duration;
}
