using Robust.Shared.Prototypes;

namespace Content.Shared.Traits;

/// <summary>
/// Display category for traits. Points and slots use the shared character budget.
/// </summary>
[Prototype]
public sealed partial class TraitCategoryPrototype : IPrototype
{
    public const string Default = "Default";

    [ViewVariables]
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    ///     Name of the trait category displayed in the UI
    /// </summary>
    [DataField]
    public LocId Name { get; private set; } = string.Empty;

    /// <summary>
    ///     Legacy YAML compatibility field. Not used by the shared trait budget.
    /// </summary>
    [DataField]
    public int? MaxTraitPoints;
}
