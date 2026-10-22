using Content.Server._DeepLagoon.InteractionPanel;
using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class AdjustArousal : EntityEffect
{
    [DataField] public float Amount = 1;

    public override void Effect(EntityEffectBaseArgs args)
    {
        var dose = args is EntityEffectReagentArgs reagent ? (float)(reagent.Quantity * reagent.Scale) : 1;
        if (!float.IsFinite(dose) || dose <= 0) return;
        args.EntityManager.System<InteractionPanelArousalSystem>().AdjustArousal(args.TargetEntity, args.TargetEntity, Amount * dose);
    }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) =>
        Loc.GetString("dl-lustwish-reagent-effect", ("amount", Amount));
}
