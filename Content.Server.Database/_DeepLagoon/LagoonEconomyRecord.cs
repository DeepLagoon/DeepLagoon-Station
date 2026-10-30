using System;
using System.Collections.Generic;

namespace Content.Server.Database;

public sealed class LagoonEconomyRecord
{
    public string Id { get; set; } = "";
    public Guid UserId { get; set; }
    public string Data { get; set; } = "{}";
}

public sealed class LagoonEconomyState
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public DateTime FirstSeen { get; set; }
    public long PlayedTicks { get; set; }
    public long IncomeTicks { get; set; }
    public int QualifiedDayCount { get; set; }
    public Dictionary<string, HashSet<Guid>> MentorWeeks { get; set; } = new();
    public bool MigratedTime { get; set; }
    public Dictionary<string, long> Days { get; set; } = new();
    public string ReadyRound { get; set; } = "";
    public long ReadyTicks { get; set; }
    public bool ReadyAwarded { get; set; }
    public int PassTier { get; set; }
    public long PassTicks { get; set; }
    public DateTime PassAccountedAt { get; set; }
    public int ExternalTier { get; set; }
    public DateTime? ExternalExpiresAt { get; set; }
    public string? OocColor { get; set; }
    public HashSet<string> Furniture { get; set; } = new();
    public Dictionary<string, int> FurnitureStock { get; set; } = new();
    // Legacy serialized value; apartment purchases no longer use a shared price credit.
    public long ApartmentCredit { get; set; }
    public HashSet<string> Apartments { get; set; } = new();
    public string ReferralCode { get; set; } = "";
    public Guid? Referrer { get; set; }
    public int ReferralStage { get; set; }
    public bool ReferrerReward { get; set; }
    public Dictionary<string, int> ReferralMonths { get; set; } = new();
    public Dictionary<string, int> DutyMonths { get; set; } = new();
    public HashSet<string> DutyDays { get; set; } = new();
    public string ApartmentLabel { get; set; } = "";
}

public sealed record LagoonPayment(Guid User, string Name, int Tier, DateTime PaidAt, bool Historical);
public sealed record LagoonTask(Guid User, Guid Creator, string Kind, int Amount, string Description, Guid? Approver = null);
public sealed record LagoonEconomySnapshot(long Balance, LagoonEconomyState State, long Awarded = 0, bool Applied = false);
public sealed record LagoonActivity(string Key, long Ticks, int Rate, string Day, string Round, bool Ready);
public sealed record LagoonEconomyPurchase(string Key, string Kind, string Product, long Price, int ExternalTier,
    int Quantity = 1, int PackSize = 0, long? ExpectedUnitPrice = null);
public sealed record LagoonEconomyReceipt(string Fingerprint);
public sealed record LagoonPublicSupporter(string Name, DateTime FirstPaidAt);

public sealed record LagoonReferral(string Name, int Stage, int Reward);
