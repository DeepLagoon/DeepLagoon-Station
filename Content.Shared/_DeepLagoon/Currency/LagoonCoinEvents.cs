using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Currency;

[Serializable, NetSerializable]
public sealed class LagoonCoinBalanceRequest : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class LagoonCoinBalanceEvent(long balance) : EntityEventArgs
{
    public long Balance = balance;
}

[Serializable, NetSerializable]
public sealed class LagoonCoinRewardEvent(long amount, long balance, string reason) : EntityEventArgs
{
    public long Amount = amount;
    public long Balance = balance;
    public string Reason = reason;
}

/// <summary>Rules shared by reward accounting and tests. HOST is not a subscription.</summary>
public static class LagoonCoinRules
{
    public const int ReadyReward = 5;
    public const int ObjectiveReward = 2;
    public static bool CountsTime(bool inRound, bool connected, bool attached, bool ghost, bool alive, bool afk)
        => inRound && connected && attached && !ghost && alive && !afk;
    public static bool HasSubscription(int actualSubscriptionTier) => actualSubscriptionTier > 0;
}
