using Content.Shared.EntityEffects;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed partial class ChangeBodyFeatureSize : EntityEffect
{
    [DataField(required: true)] public string Part = "";
    [DataField(required: true)] public int Direction;
    [DataField] public float CreationThreshold = 20;

    public override void Effect(EntityEffectBaseArgs args)
    {
        if (args is not EntityEffectReagentArgs reagent) return;
        var allowCreation = Direction > 0 && reagent.Source != null && reagent.Reagent != null &&
            reagent.Source.GetReagentQuantity(new ReagentId(reagent.Reagent.ID, null)).Float() >= CreationThreshold;
        args.EntityManager.System<BodyCustomizationSystem>().ApplyDose(args.TargetEntity, Part, Direction, reagent.Quantity, allowCreation);
    }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) =>
        Loc.GetString("reagent-effect-guidebook-body-size", ("part", Loc.GetString("dl-body-part-" + Part)),
            ("direction", Direction), ("units", BodyCustomizationSystem.UnitsPerStep), ("threshold", CreationThreshold));
}
