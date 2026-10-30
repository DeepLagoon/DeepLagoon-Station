using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Salvage;

/// <summary>Server-side expedition progression. Unlisted descendants inherit their parent's tier.</summary>
[Prototype]
public sealed partial class ExpeditionLootPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public Dictionary<int, List<EntProtoId>> Tiers = new();
    [DataField] public Dictionary<EntProtoId, EntProtoId> Replacements = new();
    [DataField] public List<EntProtoId> Tier4Rewards = new();
    [DataField] public List<EntProtoId> Tier5Rewards = new();
    // 24 half-hour expeditions/day: one T4/week and one T5/30 days on average.
    [DataField] public double Tier4Chance = 1.0 / 168;
    [DataField] public double Tier5Chance = 1.0 / 720;
}
