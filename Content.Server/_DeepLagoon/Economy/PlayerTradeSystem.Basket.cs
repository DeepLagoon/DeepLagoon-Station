using Content.Server.Database;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Economy;

public sealed partial class PlayerTradeSystem
{
    private readonly Dictionary<NetUserId, BasketPlan> _basketQuotes = new();
}
