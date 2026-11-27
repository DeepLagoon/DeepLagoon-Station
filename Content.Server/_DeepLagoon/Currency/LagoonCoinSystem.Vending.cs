using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Currency;

public sealed partial class LagoonCoinSystem
{
    /// <summary>Persist a failed-delivery refund before acknowledging it. The existing LC outbox retries it after restart.</summary>
    public async Task RefundVendingSpend(NetUserId user, int amount, string order)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(order) || order.Length > 180)
            throw new ArgumentException("Invalid LC vendor refund.");
        var reward = new Reward(user, $"refund:spend:{order}", amount, "Возврат за невыданный товар", user);
        if (await _outbox.StoreAsync(Pending(reward))) _pending.Enqueue(reward);
    }
}
