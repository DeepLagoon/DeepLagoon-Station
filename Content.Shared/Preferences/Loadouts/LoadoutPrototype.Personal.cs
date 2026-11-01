using Robust.Shared.Prototypes;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared._DeepLagoon.DiscordLink;

namespace Content.Shared.Preferences.Loadouts;

public sealed partial class LoadoutPrototype
{
    [DataField] public List<EntProtoId> PersonalItems = new();
    [DataField] public string PersonalCategory = string.Empty;
    [DataField] public int PersonalCost;
    [DataField] public bool PersonalExclusive;
    /// <summary>Give personal items to a free hand instead of equipping them in clothing slots.</summary>
    [DataField] public bool PersonalInhand;
    [DataField] public bool PersonalCustomName = true;
    [DataField] public bool PersonalCustomDescription = true;
    [DataField] public bool PersonalCustomColor;
    [DataField] public bool PersonalHeirloom;
    [DataField] public List<PersonalLoadoutRequirement> PersonalRequirements = new();
    [DataField] public DonorCategory PersonalDonor;
    [DataField] public int PersonalDonorTier = 1;
}
