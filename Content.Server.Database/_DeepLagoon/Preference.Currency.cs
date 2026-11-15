using System;

namespace Content.Server.Database;

public partial class Preference
{
    public long LagoonCoins { get; set; }
    public long LagoonCoinPlayedTicks { get; set; }
    public long LagoonCoinBonusTicks { get; set; }
}

/// <summary>An account-scoped idempotency key and audit record, committed with the wallet.</summary>
public sealed class LagoonCoinOperation
{
    public Guid UserId { get; set; }
    public string OperationId { get; set; } = "";
    public long Amount { get; set; }
    public long PlayedTicks { get; set; }
    public long SubscriberTicks { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ActorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public long BalanceAfter { get; set; }
    public bool WebhookDelivered { get; set; }
    public bool FinanceDelivered { get; set; }
    public Guid? WebhookLeaseOwner { get; set; }
    public DateTime? WebhookLeaseUntil { get; set; }
}
