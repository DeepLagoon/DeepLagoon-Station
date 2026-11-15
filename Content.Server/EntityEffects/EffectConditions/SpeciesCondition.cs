using Content.Shared.EntityEffects;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.EffectConditions;

public sealed partial class SpeciesCondition : EntityEffectCondition
{
    [DataField(required: true)]
    public ProtoId<SpeciesPrototype> Species;

    [DataField]
    public bool Invert;

    public override bool Condition(EntityEffectBaseArgs args)
    {
        var matches = args.EntityManager.TryGetComponent<HumanoidAppearanceComponent>(args.TargetEntity, out var appearance)
            && appearance.Species == Species;
        return matches ^ Invert;
    }

    public override string GuidebookExplanation(IPrototypeManager prototype)
    {
        return Loc.GetString("reagent-effect-condition-guidebook-species",
            ("species", Loc.GetString(prototype.Index(Species).Name)),
            ("invert", Invert));
    }
}
