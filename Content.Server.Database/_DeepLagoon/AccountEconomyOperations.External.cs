using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record LagoonExternalSupporter(int Tier, DateTime ObservedAt, DateTime ExpiresAt, int? DiscordTier = null, DateTime? DiscordExpiresAt = null);

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

    // Paid periods belong to the account. Removing a Discord role only changes
    // the short Discord observation, never an independently confirmed payment.
    private static async Task<LagoonExternalSupporter> WriteExternal(ServerDbContext db, Guid user,
        LagoonEconomyRecord? row, int discordTier, DateTime observedAt, DateTime discordExpiresAt,
        DateTime now, CancellationToken cancel)
    {
        if (discordExpiresAt <= now) discordTier = 0;
        var payments = await db.LagoonEconomyRecords.AsNoTracking()
            .Where(p => p.UserId == user && p.Id.StartsWith("payment:")).ToListAsync(cancel);
        var paid = payments.Select(Read<LagoonPayment>)
            .Where(p => !p.Historical && p.PaidAt <= now && p.ExpiresAt > now && p.Tier is >= 1 and <= 4)
            .OrderByDescending(p => p.Tier).ThenByDescending(p => p.ExpiresAt).FirstOrDefault();
        var tier = Math.Max(discordTier, paid?.Tier ?? 0);
        var expiresAt = tier == 0 ? now : discordTier == tier ? discordExpiresAt : now;
        if (paid?.Tier == tier && paid.ExpiresAt is { } paidExpiry && paidExpiry > expiresAt)
            expiresAt = paidExpiry;
        var lease = new LagoonExternalSupporter(tier, observedAt, expiresAt, discordTier, discordExpiresAt);
        if (row == null)
        {
            row = new LagoonEconomyRecord { Id = "external:" + user.ToString("N"), UserId = user };
            db.LagoonEconomyRecords.Add(row);
        }
        row.Data = Json(lease);
        return lease;
    }

    private static async Task ApplyPaidPeriod(ServerDbContext db, Guid user, DateTime now, CancellationToken cancel)
    {
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == "external:" + user.ToString("N"), cancel);
        var prior = row == null ? null : Read<LagoonExternalSupporter>(row);
        // Old rows contained only Discord observations. Bound that source to its
        // original 15-minute lease even when a paid period now extends access.
        var observedAt = prior?.ObservedAt ?? now;
        var discordExpiry = prior?.DiscordExpiresAt ?? (prior == null ? now :
            prior.ExpiresAt < observedAt.AddMinutes(15) ? prior.ExpiresAt : observedAt.AddMinutes(15));
        var lease = await WriteExternal(db, user, row, prior?.DiscordTier ?? prior?.Tier ?? 0,
            observedAt, discordExpiry, now, cancel);
        var (_, account, state) = await Load(db, user, now, cancel);
        AdvancePass(state, now, lease.Tier, lease.ExpiresAt);
        Save(account, state);
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
        var lease = await WriteExternal(db, user, row, tier, observedAt, observedAt.AddMinutes(15), now, cancel);
        var (_, account, state) = await Load(db, user, now, cancel);
        AdvancePass(state, now, lease.Tier, lease.ExpiresAt); Save(account, state);
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
    }
}
