using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record SpawnedCashSource(Guid PlayerId, string PlayerName, int Amount);

public sealed class BankFinancialOperation
{
    public Guid TransactionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Kind { get; set; } = "";
    public int Amount { get; set; }
    public Guid UserId { get; set; }
    public string Character { get; set; } = "";
    public int Balance { get; set; }
    public Guid? RecipientId { get; set; }
    public string? RecipientCharacter { get; set; }
    public int? RecipientBalance { get; set; }
    public string? OriginJson { get; set; }
    public bool Delivered { get; set; }

    internal static void Record(ServerDbContext db, Guid userId, Profile profile, int amount, string kind,
        IReadOnlyList<SpawnedCashSource>? origins = null, Guid? recipientId = null, Profile? recipient = null)
    {
        db.BankFinancialOperations.Add(new BankFinancialOperation
        {
            TransactionId = Guid.NewGuid(),
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
