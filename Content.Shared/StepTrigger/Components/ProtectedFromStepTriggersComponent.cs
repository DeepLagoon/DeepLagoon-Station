using Content.Shared.Inventory;
using Content.Shared.Whitelist;
using Content.Shared.StepTrigger.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared.StepTrigger.Components;

/// <summary>
/// This is used for cancelling preventable step trigger events if the user is wearing clothing in a valid slot or if the user itself has the component.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(StepTriggerImmuneSystem))]
public sealed partial class ProtectedFromStepTriggersComponent : Component, IClothingSlots
{
    /// <summary>Optional restriction on which hazards this protection blocks.</summary>
    [DataField] public EntityWhitelist? Whitelist;

    [DataField]
    public SlotFlags Slots { get; set; } = SlotFlags.FEET;
}
