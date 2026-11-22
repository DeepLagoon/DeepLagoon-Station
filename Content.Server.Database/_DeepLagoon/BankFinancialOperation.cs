using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record SpawnedCashSource(Guid PlayerId, string PlayerName, long Amount);

public sealed class BankFinancialOperation
{
    public Guid TransactionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Kind { get; set; } = "";
    public long Amount { get; set; }
    public Guid UserId { get; set; }
    public string Character { get; set; } = "";
    public long Balance { get; set; }
    public Guid? RecipientId { get; set; }
    public string? RecipientCharacter { get; set; }
    public long? RecipientBalance { get; set; }
    public string? OriginJson { get; set; }
    public bool Delivered { get; set; }

    internal static Guid Record(ServerDbContext db, Guid userId, Profile profile, long amount, string kind,
        IReadOnlyList<SpawnedCashSource>? origins = null, Guid? recipientId = null, Profile? recipient = null)
    {
        var id = Guid.NewGuid();
        db.BankFinancialOperations.Add(new BankFinancialOperation
        {
            TransactionId = id,
            CreatedAt = DateTime.UtcNow,
            Kind = kind,
            Amount = amount,
            UserId = userId,
            Character = profile.CharacterName,
            Balance = profile.BankBalance,
            RecipientId = recipientId,
            RecipientCharacter = recipient?.CharacterName,
            RecipientBalance = recipient?.BankBalance,
            OriginJson = origins is { Count: > 0 } ? JsonSerializer.Serialize(origins) : null,
        });
        return id;
    }

    public static Task<List<BankFinancialOperation>> Pending(ServerDbContext db, bool alerts,
        CancellationToken cancel)
        => db.BankFinancialOperations.AsNoTracking()
            .Where(tx => !tx.Delivered && (tx.OriginJson != null) == alerts)
            .OrderBy(tx => tx.CreatedAt).ThenBy(tx => tx.TransactionId)
            .Take(alerts ? 5 : 15).ToListAsync(cancel);

    public static Task<int> Acknowledge(ServerDbContext db, Guid[] ids, CancellationToken cancel)
        => db.BankFinancialOperations.Where(tx => !tx.Delivered && ids.Contains(tx.TransactionId))
            .ExecuteUpdateAsync(update => update.SetProperty(tx => tx.Delivered, true), cancel);
}
