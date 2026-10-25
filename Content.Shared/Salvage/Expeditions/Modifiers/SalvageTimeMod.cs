using Robust.Shared.Prototypes;

namespace Content.Shared.Salvage.Expeditions.Modifiers;

// Frontier: a modifier to manipulate the length of a given salvage expedition
[Prototype("salvageTimeMod")]
public sealed partial class SalvageTimeMod : IPrototype, ISalvageMod
{
    [IdDataField] public string ID { get; private set; } = default!;

    [DataField("desc")] public LocId Description { get; private set; } = string.Empty;

    /// <summary>
    /// Cost for difficulty modifiers.
    /// </summary>
    [DataField("cost")]
    public float Cost { get; private set; }

    [DataField("minDuration")]
    public int MinDuration = 1800;

    [DataField("maxDuration")]
    public int MaxDuration = 1830;

    // Hack: Description isn't nullable
    [DataField]
    public bool Hidden = true;
}
