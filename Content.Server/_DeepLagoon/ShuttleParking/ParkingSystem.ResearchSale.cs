using System.Linq;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Research.TechnologyDisk.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    private sealed record ResearchSale(double Price,
        HashSet<ProtoId<LatheRecipePrototype>> Recipes,
        HashSet<ProtoId<TechnologyPrototype>> Technologies, int Unsupported);

    private ResearchSale QuoteResearchSale(IEnumerable<EntityUid> entities)
    {
        var onboard = entities.ToHashSet();
        var recipes = new HashSet<ProtoId<LatheRecipePrototype>>();
        var technologies = new HashSet<ProtoId<TechnologyPrototype>>();
        var stockRecipes = new HashSet<ProtoId<LatheRecipePrototype>>();
        var stockTechs = new HashSet<ProtoId<TechnologyPrototype>>();
        foreach (var entity in onboard)
        {
            if (!TryComp<TechnologyDatabaseComponent>(entity, out var database))
                continue;
            // A lathe synced to a station server does not own the station's research.
            // Only knowledge on an onboard server is part of the ship's sale.
            if (!HasComp<ResearchServerComponent>(entity))
                continue;
            if (Prototype(entity) is { } prototype && prototype.TryComp<TechnologyDatabaseComponent>(out var stock, _components))
            {
                stockRecipes.UnionWith(stock.UnlockedRecipes);
                stockTechs.UnionWith(stock.UnlockedTechnologies);
            }
            recipes.UnionWith(database.UnlockedRecipes);
            technologies.UnionWith(database.UnlockedTechnologies);
        }
        recipes.ExceptWith(stockRecipes);
        technologies.ExceptWith(stockTechs);

        var techRecipes = technologies.Where(tech => _prototypes.HasIndex(tech))
            .ToDictionary(tech => tech, tech => _prototypes.Index(tech));
        var candidates = _prototypes.EnumeratePrototypes<EntityPrototype>()
            .Where(proto => !proto.Abstract && proto.TryComp<TechnologyDiskComponent>(out var disk, _components)
                && disk.Recipes is { Count: > 0 })
            .Select(proto =>
            {
                proto.TryComp<TechnologyDiskComponent>(out var disk, _components);
                return (Prototype: proto, Recipes: disk!.Recipes!.ToHashSet());
            })
            .Where(disk => disk.Recipes.IsSubsetOf(recipes))
            .OrderByDescending(disk => disk.Recipes.Count)
            .ThenBy(disk => _pricing.GetEstimatedPrice(disk.Prototype))
            .ThenBy(disk => disk.Prototype.ID, StringComparer.Ordinal)
            .ToArray();

        var sold = new HashSet<ProtoId<LatheRecipePrototype>>();
        var value = 0.0;
        foreach (var disk in candidates)
        {
            if (disk.Recipes.IsSubsetOf(sold))
                continue;
            value += Math.Max(0, _pricing.GetEstimatedPrice(disk.Prototype));
            sold.UnionWith(disk.Recipes);
        }
        var soldTechs = techRecipes.Where(pair => pair.Value.GenericUnlocks.Count == 0
                && pair.Value.RecipeUnlocks.Count > 0 && pair.Value.RecipeUnlocks.All(sold.Contains))
            .Select(pair => pair.Key).ToHashSet();
        return new(value, sold, soldTechs,
            recipes.Count(recipe => !sold.Contains(recipe)) + technologies.Count(tech => !soldTechs.Contains(tech)));
    }

    public (double Materials, double Research, int UnpricedResearch) PreviewStorageSale(EntityUid grid)
    {
        var excluded = ExcludedEntities(grid).ToHashSet();
        var entities = Descendants(grid).Where(entity => !excluded.Contains(entity)).ToArray();
        var research = QuoteResearchSale(entities);
        return (StoredMaterialValue(entities), research.Price, research.Unsupported);
    }
}
