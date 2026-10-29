using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Traits.Assorted.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class FlightComponent : Component
{
    [DataField, AutoNetworkedField] public bool On;
    [DataField, AutoNetworkedField] public float SpeedModifier = 1.19f;
    [DataField] public float StaminaDrainRate = 6f;
    [DataField] public float ActivationDelay = 1f;
    [DataField] public bool NeedsHands;
    [DataField] public float FlapInterval = 0.75f;
    [DataField] public EntProtoId ToggleAction = "ActionToggleFlight";
    [DataField] public EntityUid? ToggleActionEntity;
}

public sealed partial class ToggleFlightEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed partial class FlightDoAfterEvent : SimpleDoAfterEvent;
