using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record FinanceQueueId(Guid UserId, string OperationId);

public sealed record FinanceNotice(
    FinanceQueueId Id, DateTime CreatedAt, string Kind, long Amount, long Balance, string User,
    string? Character = null, string? Recipient = null, string? RecipientCharacter = null,
    int? RecipientBalance = null, SpawnedCashSource[]? Origins = null, string? Reason = null);

public partial interface IServerDbManager
{
    Task<FinanceNotice[]> ReadFinanceQueue(string topic, CancellationToken cancel);
    Task AcknowledgeFinanceQueue(string topic, FinanceQueueId[] ids, CancellationToken cancel);
}

public sealed partial class ServerDbManager
{
    public Task<FinanceNotice[]> ReadFinanceQueue(string topic, CancellationToken cancel)
        => RunDbCommand(() => _db.ReadFinanceQueue(topic, cancel));

    public Task AcknowledgeFinanceQueue(string topic, FinanceQueueId[] ids, CancellationToken cancel)
        => RunDbCommand(() => _db.AcknowledgeFinanceQueue(topic, ids, cancel));
}

public abstract partial class ServerDbBase
{
    internal async Task<FinanceNotice[]> ReadFinanceQueue(string topic, CancellationToken cancel)
    {
        await using var handle = await GetDb(cancel);
        var db = handle.DbContext;
        if (topic == "lc")
        {
            var coins = await db.LagoonCoinOperations.AsNoTracking()
                .Where(row => !row.FinanceDelivered && row.Amount != 0)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.UserId).ThenBy(row => row.OperationId)
                .Take(15).ToListAsync(cancel);
            if (coins.Count == 0)
                return [];
            var players = await FinanceNames(db, coins.Select(row => row.UserId), cancel);
            return coins.Select(row => new FinanceNotice(
                new FinanceQueueId(row.UserId, row.OperationId), UtcFinanceTime(row.CreatedAt), "lc",
                row.Amount, row.BalanceAfter, players.GetValueOrDefault(row.UserId) ?? row.UserId.ToString(),
                Reason: row.Reason)).ToArray();
        }

        var rows = await BankFinancialOperation.Pending(db, topic == "alerts", cancel);
        if (rows.Count == 0)
            return [];
        var users = rows.Select(row => row.UserId)
            .Concat(rows.Where(row => row.RecipientId.HasValue).Select(row => row.RecipientId!.Value));
        var names = await FinanceNames(db, users, cancel);
        return rows.Select(row => new FinanceNotice(
            new FinanceQueueId(row.UserId, row.TransactionId.ToString("N")), UtcFinanceTime(row.CreatedAt),
            row.Kind, row.Amount, row.Balance, names.GetValueOrDefault(row.UserId) ?? row.UserId.ToString(),
            row.Character, row.RecipientId is { } recipient ? names.GetValueOrDefault(recipient) ?? recipient.ToString() : null,
            row.RecipientCharacter, row.RecipientBalance,
            row.OriginJson == null ? null : JsonSerializer.Deserialize<SpawnedCashSource[]>(row.OriginJson))).ToArray();
    }

    internal async Task AcknowledgeFinanceQueue(string topic, FinanceQueueId[] ids, CancellationToken cancel)
    {
        await using var handle = await GetDb(cancel);
        var db = handle.DbContext;
        if (topic != "lc")
        {
            await BankFinancialOperation.Acknowledge(db, ids.Select(id => Guid.ParseExact(id.OperationId, "N")).ToArray(), cancel);
            return;
        }

        // Group by UID so each update uses the primary key. Each acknowledgement is idempotent.
        foreach (var group in ids.GroupBy(id => id.UserId))
        {
            var user = group.Key;
            var operationIds = group.Select(id => id.OperationId).ToArray();
            await db.LagoonCoinOperations.Where(row => !row.FinanceDelivered && row.UserId == user && operationIds.Contains(row.OperationId))
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.FinanceDelivered, true), cancel);
        }
    }

    private static Task<Dictionary<Guid, string>> FinanceNames(ServerDbContext db, IEnumerable<Guid> users,
        CancellationToken cancel)
    {
        var keys = users.Distinct().ToArray();
        return db.Player.AsNoTracking().Where(player => keys.Contains(player.UserId))
            .ToDictionaryAsync(player => player.UserId, player => player.LastSeenUserName, cancel);
    }

    private static DateTime UtcFinanceTime(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc);
}
