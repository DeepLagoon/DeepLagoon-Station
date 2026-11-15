using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Materials;
using Content.Client.Storage.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.Lathe;

/// <summary>
/// JSON for the TGUI lathe menu. The recipes (names, costs, categories) change only when the set of
/// recipes or the machine's multipliers do, so they are built once per such set and cached for the
/// session. Everything that moves (stock, queue, progress) is built on each change, and is small.
/// All text arrives localized and all costs arrive adjusted by the machine's multiplier, so the page
/// never repeats the (floating point) rounding of the server.
/// </summary>
public sealed class LatheTguiData
{
    private static readonly string[] LabelKeys =
    [
        "dl-lathe-amount-decrease",
        "dl-lathe-amount-increase",
        "dl-lathe-category-all",
        "dl-lathe-category-none",
        "dl-lathe-count",
        "dl-lathe-current",
        "dl-lathe-current-idle",
        "dl-lathe-current-waiting",
        "dl-lathe-decimal-separator",
        "dl-lathe-eject",
        "dl-lathe-legend-queue",
        "dl-lathe-legend-recipe",
        "dl-lathe-legend-stock",
        "dl-lathe-loop",
        "dl-lathe-loop-tip",
        "dl-lathe-materials",
        "dl-lathe-materials-empty",
        "dl-lathe-max",
        "dl-lathe-multiplier-material",
        "dl-lathe-multiplier-time",
        "dl-lathe-only-available",
        "dl-lathe-queue",
        "dl-lathe-queue-action",
        "dl-lathe-queue-cancel",
        "dl-lathe-queue-empty",
        "dl-lathe-queue-unavailable",
        "dl-lathe-recipes-count",
        "dl-lathe-search-clear",
        "dl-lathe-search-empty",
        "dl-lathe-search-placeholder",
        "dl-lathe-servers",
        "dl-lathe-short",
        "dl-lathe-short-after-queue",
        "dl-lathe-silo-linked",
        "dl-lathe-skip",
        "dl-lathe-skip-tip",
        "dl-lathe-time",
        "dl-lathe-time-unit",
        "dl-lathe-unit-reagent",
        "dl-lathe-yields",
    ];

    // Static data of a few lathes is kept for the session; this many sets are enough for any round.
    private const int StaticCacheLimit = 16;
    private static readonly Dictionary<string, string> StaticCache = new();

    private readonly EntityUid _owner;
    private readonly IEntityManager _entities;
    private readonly IPrototypeManager _prototypes = IoCManager.Resolve<IPrototypeManager>();
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private readonly SharedLatheSystem _lathe;
    private readonly MaterialStorageSystem _materialStorage;
    private readonly SharedContainerSystem _container;
    private readonly SharedSolutionContainerSystem _solution;

    /// <summary>True when some recipe needs entities from the machine's storage or reagents from its beaker.</summary>
    public bool HasExtraRequirements { get; private set; }

    public LatheTguiData(EntityUid owner, IEntityManager entities)
    {
        _owner = owner;
        _entities = entities;
        _lathe = entities.System<SharedLatheSystem>();
        _materialStorage = entities.System<MaterialStorageSystem>();
        _container = entities.System<SharedContainerSystem>();
        _solution = entities.System<SharedSolutionContainerSystem>();
    }

    public static void ClearCache() => StaticCache.Clear();

    /// <summary>Identifies the static data: the recipes, the multipliers and the language.</summary>
    public string StaticKey(LatheUpdateState state, LatheComponent lathe)
    {
        var culture = IoCManager.Resolve<ILocalizationManager>().DefaultCulture?.Name;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        // The materials the machine accepts decide which materials the page learns the names and icons of.
        var accepted = _entities.TryGetComponent<MaterialStorageComponent>(_owner, out var storage) && storage.MaterialWhiteList is { } whitelist
            ? string.Join(',', whitelist.Select(material => material.Id))
            : string.Empty;
        return string.Join(',', state.Recipes.Select(recipe => recipe.Id)) + '|' + accepted + '|' +
            Math.Round(lathe.FinalMaterialUseMultiplier, 5).ToString(invariant) + '|' +
            Math.Round(lathe.TimeMultiplier * lathe.FinalTimeMultiplier, 5).ToString(invariant) + '|' + culture;
    }

    /// <summary>Ids of the recipes the menu may ask icons for or queue.</summary>
    public HashSet<string> RecipeIds(LatheUpdateState state)
    {
        var ids = new HashSet<string>();
        foreach (var recipe in state.Recipes)
            ids.Add(recipe.Id);
        HasExtraRequirements = state.Recipes.Any(id =>
            _prototypes.TryIndex(id, out var recipe) && (recipe.Entities.Count > 0 || recipe.Reagents.Count > 0));
        return ids;
    }

    public string BuildStatic(LatheUpdateState state, LatheComponent lathe, string key)
    {
        if (StaticCache.TryGetValue(key, out var cached))
            return cached;
        if (StaticCache.Count >= StaticCacheLimit)
            StaticCache.Clear();

        var recipes = new List<LatheRecipePrototype>();
        foreach (var id in state.Recipes)
        {
            if (_prototypes.TryIndex(id, out var recipe))
                recipes.Add(recipe);
        }

        var materialIds = new HashSet<string>();
        var categoryIds = new HashSet<string>();
        foreach (var recipe in recipes)
        {
            foreach (var (material, _) in recipe.Materials)
                materialIds.Add(material);
            foreach (var (material, _) in recipe.MaterialResult)
                materialIds.Add(material);
            foreach (var category in recipe.Categories)
                categoryIds.Add(category);
        }

        if (_entities.TryGetComponent<MaterialStorageComponent>(_owner, out var storage) &&
            storage.MaterialWhiteList is { } whitelist)
        {
            foreach (var material in whitelist)
                materialIds.Add(material);
        }

        var labels = new TguiData();
        foreach (var labelKey in LabelKeys)
            labels.String(labelKey, Loc.GetString(labelKey));

        var materials = materialIds
            .Where(id => _prototypes.HasIndex<MaterialPrototype>(id))
            .Select(id => BuildMaterial(_prototypes.Index<MaterialPrototype>(id)));
        var categories = categoryIds
            .Where(id => _prototypes.HasIndex<LatheCategoryPrototype>(id))
            .Select(id => new TguiData()
                .String("id", id)
                .String("name", Loc.GetString(_prototypes.Index<LatheCategoryPrototype>(id).Name)));

        var materialUse = lathe.FinalMaterialUseMultiplier;
        var time = lathe.TimeMultiplier * lathe.FinalTimeMultiplier;
        var data = new TguiData()
            .Object("labels", labels)
            .Number("materialMultiplier", materialUse)
            .Number("timeMultiplier", lathe.FinalTimeMultiplier)
            .Array("materials", materials)
            .Array("categories", categories)
            .Array("recipes", recipes.Select(recipe => BuildRecipe(recipe, materialUse, time)));
        return StaticCache[key] = "{\"static_data\":" + data + "}";
    }

    public string BuildDynamic(LatheUpdateState state, LatheComponent lathe)
    {
        var stock = _materialStorage.GetStoredMaterials(_owner);
        var reserved = new Dictionary<string, int>();
        foreach (var batch in state.Queue)
        {
            var remaining = batch.ItemsRequested - batch.ItemsPrinted;
            if (remaining <= 0)
                continue;
            foreach (var (material, needed) in batch.Recipe.Materials)
            {
                var cost = _lathe.GetMaterialCost(batch.Recipe, material, needed, lathe.FinalMaterialUseMultiplier);
                reserved[material.Id] = reserved.GetValueOrDefault(material.Id) + cost * remaining;
            }
        }

        var canEject = _entities.TryGetComponent<MaterialStorageComponent>(_owner, out var storage) &&
            storage.CanEjectStoredMaterials;
        var silo = _entities.TryGetComponent<OreSiloClientComponent>(_owner, out var client) && client.Silo != null;

        return new TguiData()
            .String("name", _entities.GetComponent<MetaDataComponent>(_owner).EntityName)
            .Bool("looping", state.Looping)
            .Bool("skipping", state.Skipping)
            .Bool("canEject", canEject)
            .Bool("silo", silo)
            .Bool("servers", lathe.DynamicPacks.Count > 0)
            .Number("defaultAmount", Math.Max(1, lathe.DefaultProductionAmount))
            .Array("stock", stock.Where(pair => pair.Value > 0).Select(pair => BuildStock(pair.Key, pair.Value)))
            .Array("reserved", reserved.Select(pair => new TguiData().String("id", pair.Key).Number("n", pair.Value)))
            .Array("entityStock", EntityStock())
            .Array("reagentStock", ReagentStock(lathe))
            .Array("queue", state.Queue.Select(batch => new TguiData()
                .Number("index", batch.Index)
                .String("id", batch.Recipe.ID)
                .String("name", RecipeName(batch.Recipe))
                .Number("printed", batch.ItemsPrinted)
                .Number("requested", batch.ItemsRequested)))
            .Object("current", BuildCurrent(state))
            .ToString();
    }

    /// <summary>
    /// What the menu shows that changes on the client without the server sending a menu state (the machine's
    /// stock, its multipliers, its silo link, what lies in its storage and beaker). The window compares
    /// it a few times a second to know when to publish again.
    /// </summary>
    public string LiveSignature(LatheComponent lathe)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var (material, amount) in _materialStorage.GetStoredMaterials(_owner).OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
            builder.Append(material.Id).Append('=').Append(amount).Append(';');
        // StringBuilder.Append(float) is not in the sandbox whitelist, so the numbers go in as text.
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        builder.Append(Math.Round(lathe.FinalMaterialUseMultiplier, 5).ToString(invariant)).Append(';')
            .Append(Math.Round(lathe.FinalTimeMultiplier, 5).ToString(invariant)).Append(';');
        builder.Append(_entities.TryGetComponent<OreSiloClientComponent>(_owner, out var client) && client.Silo != null).Append(';');
        foreach (var entry in EntityStock())
            builder.Append(entry).Append(';');
        foreach (var entry in ReagentStock(lathe))
            builder.Append(entry).Append(';');
        return builder.ToString();
    }

    /// <summary>Icons for the recipes the page asks for (it shows them as it scrolls).</summary>
    public string BuildIcons(IEnumerable<string> recipeIds)
    {
        var icons = recipeIds
            .Where(id => _prototypes.HasIndex<LatheRecipePrototype>(id))
            .Select(id => new TguiData()
                .String("id", id)
                .Array("layers", TguiRecipeIcons.Layers(_prototypes.Index<LatheRecipePrototype>(id))));
        return "{\"data\":" + new TguiData().Object("iconBatch", new TguiData().Array("icons", icons)) + "}";
    }

    private TguiData BuildMaterial(MaterialPrototype material)
    {
        return new TguiData()
            .String("id", material.ID)
            .String("name", Loc.GetString(material.Name))
            .Number("sheet", _materialStorage.GetSheetVolume(material))
            .String("color", material.Color.ToHexNoAlpha())
            .String("icon", TguiRecipeIcons.Images.Frame(material.Icon));
    }

    private TguiData BuildRecipe(LatheRecipePrototype recipe, float materialUse, float time)
    {
        var data = new TguiData()
            .String("id", recipe.ID)
            .String("name", RecipeName(recipe))
            .String("desc", _lathe.GetRecipeDescription(recipe))
            .String("cats", string.Join(',', recipe.Categories.Select(category => category.Id)))
            .Number("time", (float) recipe.CompleteTime.TotalSeconds * time)
            .Number("count", recipe.ResultCount)
            .Array("mats", recipe.Materials.Select(pair => new TguiData()
                .String("id", pair.Key)
                .Number("n", _lathe.GetMaterialCost(recipe, pair.Key, pair.Value, materialUse))));

        if (recipe.Entities.Count > 0)
        {
            data.Array("ents", recipe.Entities.Select(pair => new TguiData()
                .String("id", pair.Key)
                .String("name", _prototypes.TryIndex(pair.Key, out var entity) ? entity.Name : pair.Key.Id)
                .Number("n", pair.Value)));
        }

        if (recipe.Reagents.Count > 0)
        {
            data.Array("reagents", recipe.Reagents.Select(pair => new TguiData()
                .String("id", pair.Key)
                .String("name", ReagentName(pair.Key))
                .Number("n", pair.Value.Float())));
        }

        if (recipe.MaterialResult.Count > 0)
        {
            data.Array("yields", recipe.MaterialResult.Select(pair => new TguiData()
                .String("id", pair.Key)
                .Number("n", pair.Value)));
        }

        return data;
    }

    private TguiData BuildStock(string id, int amount)
    {
        var name = id;
        var text = amount.ToString();
        if (_prototypes.TryIndex<MaterialPrototype>(id, out var material))
        {
            name = Loc.GetString(material.Name);
            var sheets = amount / (float) _materialStorage.GetSheetVolume(material);
            text = Loc.GetString("lathe-menu-material-amount", ("amount", sheets), ("unit", Loc.GetString(material.Unit)));
        }

        return new TguiData().String("id", id).String("name", name).String("text", text).Number("n", amount);
    }

    private TguiData BuildCurrent(LatheUpdateState state)
    {
        if (state.CurrentlyProducing is not { } recipe)
            return new TguiData();

        var data = new TguiData()
            .String("id", recipe.ID)
            .String("name", RecipeName(recipe));

        if (state.ProductionStart is { } start && state.ProductionLength is { } length)
        {
            var elapsed = Math.Clamp((_timing.CurTime - start).TotalSeconds, 0, length.TotalSeconds);
            data.Bool("active", true)
                .String("key", start.Ticks.ToString())
                .Number("total", (float) length.TotalSeconds)
                .Number("elapsed", (float) elapsed);
        }
        else
        {
            data.Bool("active", false);
        }

        return data;
    }

    private string RecipeName(LatheRecipePrototype recipe)
    {
        var name = _lathe.GetRecipeName(recipe);
        return string.IsNullOrWhiteSpace(name) ? recipe.ID : name;
    }

    private string ReagentName(string id) =>
        _prototypes.TryIndex<ReagentPrototype>(id, out var reagent) ? reagent.LocalizedName : id;

    // The same sources as the native menu: the machine's storage for entities, the first thing in the
    // beaker slot for reagents.
    private IEnumerable<TguiData> EntityStock()
    {
        if (!HasExtraRequirements ||
            !_entities.TryGetComponent<EntityStorageComponent>(_owner, out var storage))
        {
            yield break;
        }

        var counts = new Dictionary<string, int>();
        foreach (var contained in storage.Contents.ContainedEntities)
        {
            if (_entities.GetComponent<MetaDataComponent>(contained).EntityPrototype is not { } prototype)
                continue;
            counts[prototype.ID] = counts.GetValueOrDefault(prototype.ID) +
                (_entities.TryGetComponent<StackComponent>(contained, out var stack) ? stack.Count : 1);
        }

        foreach (var (id, count) in counts)
            yield return new TguiData().String("id", id).Number("n", count);
    }

    private IEnumerable<TguiData> ReagentStock(LatheComponent lathe)
    {
        if (!HasExtraRequirements ||
            lathe.ReagentOutputSlotId is not { } slotId ||
            !_container.TryGetContainer(_owner, slotId, out var container) ||
            container.ContainedEntities.Count == 0 ||
            !_solution.TryGetDrainableSolution(container.ContainedEntities[0], out _, out var solution))
        {
            yield break;
        }

        foreach (var reagent in solution.Contents)
            yield return new TguiData().String("id", reagent.Reagent.Prototype).Number("n", reagent.Quantity.Float());
    }
}
