using Content.Shared.Examine;
using Content.Shared.Whitelist;
using Content.Shared.Inventory;
using Content.Shared.StepTrigger.Components;

namespace Content.Shared.StepTrigger.Systems;

public sealed partial class StepTriggerImmuneSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<PreventableStepTriggerComponent, StepTriggerAttemptEvent>(OnStepTriggerClothingAttempt);
        SubscribeLocalEvent<PreventableStepTriggerComponent, ExaminedEvent>(OnExamined);
    }

    private void OnStepTriggerClothingAttempt(Entity<PreventableStepTriggerComponent> ent, ref StepTriggerAttemptEvent args)
    {
        if ((TryComp<ProtectedFromStepTriggersComponent>(args.Tripper, out var innate)
             && _whitelist.IsWhitelistPassOrNull(innate.Whitelist, ent.Owner))
            || (_inventory.TryGetInventoryEntity<ProtectedFromStepTriggersComponent>(args.Tripper, out var clothing)
                && _whitelist.IsWhitelistPassOrNull(clothing.Comp!.Whitelist, ent.Owner)))
        {
            args.Cancelled = true;
        }
    }

    private void OnExamined(EntityUid uid, PreventableStepTriggerComponent component, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("clothing-required-step-trigger-examine"));
    }
}
