using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class EconomyOperations
{
    private const string TradeCycleId = "economy:trade-cycle:v1";

    /// <summary>Reuses the durable cycle after a crash. A completed cycle can open only after all returns were settled.</summary>
    public static async Task<TradeCycle> BeginTradeCycle(ServerDbContext db, bool allowNew, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var (row, cycle) = await Load<TradeCycle>(db, TradeCycleId, Guid.Empty, cancel);
        var pending = await db.LagoonEconomyRecords.AnyAsync(r => r.Id.StartsWith(OfferPrefix), cancel);
        if (cycle.Id == Guid.Empty)
        {
            cycle.Id = Guid.NewGuid();
            // Adopt existing live listings on upgrade; startup must never be interpreted as an end of round.
            foreach (var listing in await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(OfferPrefix)).ToListAsync(cancel))
            {
                var offer = JsonSerializer.Deserialize<TradeOffer>(listing.Data)!;
                offer.Expires = long.MaxValue;
                listing.Data = JsonSerializer.Serialize(offer);
            }
        }
        else if (cycle.Closed && allowNew && !pending)
            cycle = new() { Id = Guid.NewGuid() };
        cycle.PendingOffers = pending;
        row.Data = JsonSerializer.Serialize(cycle);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return cycle;
    }

    /// <summary>
    /// Commits the end marker and makes every old listing untradeable in one small transaction.
    /// Existing bounded expiry processing performs durable, idempotent refunds/returns afterwards.
    /// </summary>
    public static async Task<TradeCycle> CloseTradeCycle(ServerDbContext db, Guid expected, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var (row, cycle) = await Load<TradeCycle>(db, TradeCycleId, Guid.Empty, cancel);
        if (expected != Guid.Empty && cycle.Id != expected) return cycle;
        if (cycle.Id == Guid.Empty) cycle.Id = Guid.NewGuid();
        if (!cycle.Closed)
        {
            cycle.Closed = true;
            foreach (var listing in await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(OfferPrefix)).ToListAsync(cancel))
            {
                var offer = JsonSerializer.Deserialize<TradeOffer>(listing.Data)!;
                offer.Expires = 0;
                offer.ClosureReason = "round_end";
                listing.Data = JsonSerializer.Serialize(offer);
            }
        }
        row.Data = JsonSerializer.Serialize(cycle);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return cycle;
    }

    private static async Task RequireTradeCycle(ServerDbContext db, Guid expected, CancellationToken cancel)
    {
        var json = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id == TradeCycleId).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        if (json == null)
        {
            if (expected != Guid.Empty) throw new InvalidOperationException("round_closed");
            return; // Compatibility with existing standalone database callers.
        }
        var cycle = JsonSerializer.Deserialize<TradeCycle>(json)!;
        if (cycle.Closed || expected != Guid.Empty && cycle.Id != expected)
            throw new InvalidOperationException("round_closed");
    }
}
