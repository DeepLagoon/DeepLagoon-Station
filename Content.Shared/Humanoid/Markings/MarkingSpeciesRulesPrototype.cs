using Robust.Shared.Prototypes;
using Content.Shared.Humanoid.Prototypes;

namespace Content.Shared.Humanoid.Markings;

[Prototype]
public sealed partial class MarkingSpeciesRulesPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public List<ProtoId<MarkingPrototype>> Common = new();
    [DataField] public Dictionary<ProtoId<SpeciesPrototype>, List<ProtoId<MarkingPrototype>>> Species = new();
    [DataField] public List<ProtoId<MarkingPrototype>> GeneticOnly = new();
}
