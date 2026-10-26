using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record LagoonExternalSupporter(int Tier, DateTime ObservedAt, DateTime ExpiresAt);

public static partial class AccountEconomyOperations
{
    private static async Task AdvanceExternalPass(ServerDbContext db, Guid user, LagoonEconomyState state, int rawTier, DateTime now, CancellationToken cancel)
    {
        var row = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == "external:" + user.ToString("N"), cancel);
        var lease = row == null ? null : Read<LagoonExternalSupporter>(row);
        if (lease != null && lease.ExpiresAt > now && lease.Tier >= rawTier)
            AdvancePass(state, now, lease.Tier, lease.ExpiresAt);
        else
            AdvancePass(state, now, rawTier);
    }

    public static async Task<int> ExternalTier(ServerDbContext db, Guid user, DateTime now, CancellationToken cancel = default)
    {
        var row = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == "external:" + user.ToString("N"), cancel);
        if (row == null) return 0;
        var lease = Read<LagoonExternalSupporter>(row);
        return lease.ExpiresAt > now ? lease.Tier : 0;
    }
    public static async Task SyncExternal(ServerDbContext db, Guid user, int tier, DateTime observedAt, DateTime now,
        CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (tier is < 0 or > 4 || observedAt < now.AddMinutes(-2) || observedAt > now.AddSeconds(30)) throw new ArgumentException("Stale supporter observation.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var key = "external:" + user.ToString("N");
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == key, cancel);
        if (row != null && Read<LagoonExternalSupporter>(row).ObservedAt >= observedAt) return;
        if (row == null) { row = new LagoonEconomyRecord { Id = key, UserId = user }; db.LagoonEconomyRecords.Add(row); }
        row.Data = Json(new LagoonExternalSupporter(tier, observedAt, observedAt.AddMinutes(15)));
        var (_, account, state) = await Load(db, user, now, cancel);
        AdvancePass(state, now, tier, observedAt.AddMinutes(15)); Save(account, state);
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
    }
}
