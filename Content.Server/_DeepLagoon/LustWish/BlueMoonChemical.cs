using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class BlueMoonChemical : EntityEffect
{
    [DataField(required: true)] public string Chemical = default!;

    public override void Effect(EntityEffectBaseArgs args)
    {
        if (args is not EntityEffectReagentArgs reagent || reagent.Reagent == null) return;
        var concentration = reagent.Source == null ? (float)reagent.Quantity
            : (float)reagent.Source.GetTotalPrototypeQuantity(reagent.Reagent.ID);
        args.EntityManager.System<InteractionPanelArousalSystem>().ApplyBlueMoonChemical(args.TargetEntity, Chemical, concentration);
    }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) =>
        Loc.GetString("dl-arousal-chemical-effect");
}
