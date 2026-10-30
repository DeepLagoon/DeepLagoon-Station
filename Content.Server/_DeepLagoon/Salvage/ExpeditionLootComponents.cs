using Content.Shared.Salvage;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Salvage;

/// <summary>Travels with unopened loot, including after the expedition map is deleted.</summary>
[RegisterComponent]
public sealed partial class ExpeditionLootOriginComponent : Component
{
    [DataField] public DifficultyRating Difficulty;
    [DataField] public int MaximumTier = 1;
    [DataField] public EntityUid Expedition;
}

/// <summary>A single shared rarity roll, never a fresh roll for each crate, NPC or opening.</summary>
[RegisterComponent]
public sealed partial class ExpeditionLootBudgetComponent : Component
{
    [DataField] public int RareTier;
    [DataField] public bool Claimed;
}

[RegisterComponent]
public sealed partial class ExpeditionRareDropComponent : Component
{
    [DataField] public List<EntProtoId> Candidates = new();
    [DataField] public bool Processed;
}
