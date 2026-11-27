using Robust.Shared.Serialization;
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.Cargo;

/// <summary>
/// A data structure for storing currently available bounties.
/// </summary>
[DataDefinition, NetSerializable, Serializable]
public readonly partial record struct CargoBountyData
{
    [DataField] public Guid Instance { get; init; } = Guid.NewGuid();
    [DataField] public int FundedReward { get; init; } = -1;
    [DataField] public Content.Shared._NF.Bank.Components.SectorBankAccount FoodAccount { get; init; }
    /// <summary>
    /// A unique id used to identify the bounty
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The prototype containing information about the bounty.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField(required: true)]
    public ProtoId<CargoBountyPrototype> Bounty { get; init; } = string.Empty;

    public CargoBountyData(CargoBountyPrototype bounty, int uniqueIdentifier)
    {
        Bounty = bounty.ID;
        Id = $"{bounty.IdPrefix}{uniqueIdentifier:D3}";
    }
}
