using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Client.Cargo.UI;

internal static class BountyPresentation
{
    public static string Reward(IPrototypeManager prototypes, CargoBountyPrototype bounty, int fundedReward)
    {
        var reward = fundedReward >= 0 ? fundedReward : bounty.Reward;
        var prototype = fundedReward >= 0 ? "SpaceCash" : bounty.RewardProto.Id;
        var currency = prototypes.TryIndex<EntityPrototype>(prototype, out var item) ? item.Name : prototype;
        return Loc.GetString("dl-economy-bounty-reward", ("amount", reward), ("currency", currency));
    }
}
