using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.BodyCustomization;

public sealed partial class ChangeBodyFeatureSize : EntityEffect
{
    [DataField(required: true)] public string Part = "";
    [DataField(required: true)] public int Direction;

    public override void Effect(EntityEffectBaseArgs args)
    {
        if (args is not EntityEffectReagentArgs reagent) return;
        args.EntityManager.System<BodyCustomizationSystem>().ApplyDose(args.TargetEntity, Part, Direction, reagent.Quantity);
    }

    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) =>
        Loc.GetString("reagent-effect-guidebook-body-size", ("part", Loc.GetString("dl-body-part-" + Part)),
            ("direction", Direction), ("units", BodyCustomizationSystem.UnitsPerStep));
}
