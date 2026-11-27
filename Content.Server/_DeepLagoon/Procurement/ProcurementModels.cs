namespace Content.Server._DeepLagoon.Procurement;

// Server-only snapshots: the worker must never dereference entities or access ECS.
public enum ProcurementCategory { Materials, Industry, Recovery, Gas, Food, Unclassified, Principal }

public sealed record ProcurementItem(int Entity, int? Parent, string? Prototype, int Quantity,
    string? Stack, ProcurementCategory SuggestedCategory, double? OwnBaseAppraisal, double? OwnPayout,
    Dictionary<string, double> Materials, Dictionary<string, double> StoredMaterials,
    Dictionary<string, double> GasMoles);

public sealed record ProcurementRoot(int Entity, double BaseAppraisal, double Payout,
    ProcurementCategory SuggestedCategory);

public sealed record ProcurementValuation(double OwnBaseAppraisal, double OwnPayout);

public sealed record ProcurementSale(Guid Id, long UtcMilliseconds, int Round, Guid? Player,
    string? Character, string? Console, ProcurementRoot[] Roots, ProcurementItem[] Items,
    long CashPayout, long ReturnedPrincipal, Dictionary<string, long> FactionAccruals,
    bool ManifestComplete, string Currency = "Credit");

public sealed record ProcurementParkingManifest(Guid Operation, Guid Player, int Slot, long Payout,
    double ConfiscatedAppraisal, double MaterialAppraisal, double ResearchAppraisal,
    Dictionary<string, double> Materials, int UnpricedResearch, bool ManifestComplete = true);

public sealed record ProcurementObservation(Guid Id, long UtcMilliseconds, int Round, string Kind,
    ProcurementSale? Sale = null, ProcurementParkingManifest? Parking = null,
    Guid? Player = null, long Amount = 0, string? Account = null, string? Reason = null,
    long? Balance = null, int ActivePlayers = 0, int ConnectedPlayers = 0,
    string? Currency = null, int? Slot = null, Guid? Parent = null);

public sealed record ProcurementPolicy(string Id, decimal HourlyRate, decimal ProtectedShare,
    decimal DailyProtectedLimit)
{
    public static ProcurementPolicy[] Candidates =>
    [
        new("v1-10000-30-20000", 10000, .30m, 20000),
        new("v1-10000-40-60000", 10000, .40m, 60000),
        new("v1-20000-30-20000", 20000, .30m, 20000),
        new("v1-20000-40-60000", 20000, .40m, 60000),
    ];
}

public sealed record ProcurementShadowDecision(string Policy, bool Accepted, string Reason,
    decimal Cost, decimal ProtectedUsed, decimal GeneralUsed);

public sealed class ProcurementSaleObserved(ProcurementSale sale) : EntityEventArgs
{
    public readonly ProcurementSale Sale = sale;
}
