using System.Linq;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.Research.Systems;

public sealed partial class ResearchSystem
{
    internal void ResetStoredKnowledge(EntityUid uid, TechnologyDatabaseComponent database)
    {
        // Replace lists rather than mutate: synced clients may alias the server's collection.
        TechnologyDatabaseComponent? stock = null;
        if (Prototype(uid) is { } prototype)
            prototype.TryComp(out stock, EntityManager.ComponentFactory);
        database.UnlockedRecipes = stock?.UnlockedRecipes.ToList() ?? [];
        database.UnlockedTechnologies = stock?.UnlockedTechnologies.ToList() ?? [];
        database.MainDiscipline = stock?.MainDiscipline;
        database.CurrentTechnologyCards = [];
        UpdateTechnologyCards(uid, database);
        Dirty(uid, database);
    }
}
