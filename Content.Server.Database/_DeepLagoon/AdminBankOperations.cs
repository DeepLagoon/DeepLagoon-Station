using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record AdminBankAdjustment(Guid Operation, int Slot, int Profile, long ExpectedBalance, long Amount,
    Guid? ActorId, string Actor, string Target);
public sealed record AdminBankAudit(Guid? ActorId, string Actor, string Target, int Slot, int Profile, long Before, long After);
public readonly record struct AdminBankResult(long Balance, bool Applied);

/// <summary>One committed balance adjustment and one durable finance notification, including administrator attribution.</summary>
public static class AdminBankOperations
{
    public const string Kind = "bank_admin_adjustment";
    public const string AuditPrefix = "bank:admin:audit:";
    private sealed record Receipt(AdminBankAdjustment Request, AdminBankAudit Audit);

    public static async Task<AdminBankResult> Adjust(ServerDbContext db, Guid user, AdminBankAdjustment request,
        CancellationToken cancel = default)
    {
        if (user == Guid.Empty || request.Operation == Guid.Empty || request.Slot < 0 || request.Profile <= 0 ||
            request.ExpectedBalance < 0 || request.Amount is 0 or long.MinValue ||
            string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 256)
            throw new ArgumentException("Invalid administrative bank adjustment.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == request.Slot, cancel);
        if (profile.Id != request.Profile)
            throw new InvalidOperationException("Character profile changed; retry the command.");
        var key = AuditPrefix + request.Operation.ToString("N");
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(row => row.Id == key, cancel);
        if (previous != null)
        {
            if (previous.UserId != user || JsonSerializer.Deserialize<Receipt>(previous.Data)!.Request != request)
                throw new InvalidOperationException("Administrative bank operation identifier reused.");
            return new(profile.BankBalance, false);
        }
        if (profile.BankBalance != request.ExpectedBalance)
            throw new InvalidOperationException("Character bank balance changed; retry the command.");
        var balance = checked(profile.BankBalance + request.Amount);
        if (balance < 0)
            throw new InvalidOperationException("Insufficient character bank balance.");
        var audit = new AdminBankAudit(request.ActorId, request.Actor, request.Target, request.Slot, request.Profile,
            profile.BankBalance, balance);
        profile.BankBalance = balance;
        db.BankFinancialOperations.Add(new BankFinancialOperation
        {
            TransactionId = request.Operation, CreatedAt = DateTime.UtcNow, Kind = Kind, Amount = request.Amount,
            UserId = user, Character = profile.CharacterName, Balance = balance,
        });
        db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = key, UserId = user,
            Data = JsonSerializer.Serialize(new Receipt(request, audit)) });
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);
        return new(balance, true);
    }

    public static AdminBankAudit ReadAudit(string data) => JsonSerializer.Deserialize<Receipt>(data)!.Audit;
}
