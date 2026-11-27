using System.Linq;
using Content.Server.Nutrition.Components;
using Content.Shared.Chemistry.EntitySystems;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    [Dependency] private SharedSolutionContainerSystem _foodSolutions = default!;

    private bool HasMealNutrients(EntityUid entity, int minimum)
    {
        if (HasComp<Content.Shared.Body.Organ.OrganComponent>(entity) ||
            HasComp<Content.Shared.Body.Part.BodyPartComponent>(entity) ||
            HasComp<Content.Server.Salvage.ExpeditionSalvageComponent>(entity)) return false;
        if (!TryComp<FoodComponent>(entity, out var food) || food.RequiresSpecialDigestion ||
            !_foodSolutions.TryGetSolution(entity, food.Solution, out _, out var solution)) return false;
        return solution.Contents.Where(r => r.Reagent.Prototype.Id is "Nutriment" or "Protein" or "Vitamin")
            .Sum(r => r.Quantity.Double()) >= minimum;
    }
}
