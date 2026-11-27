using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record TradeComment(Guid Id, Guid User, string Author, string Text, long Time);

public static partial class EconomyOperations
{
    private const string CommentPrefix = "economy:comment:";
    public static async Task<TradeComment[]> Comments(ServerDbContext db, Guid offer, long now, CancellationToken cancel = default)
    {
        await RequireTradeCycle(db, Guid.Empty, cancel);
        var (_, listing) = await Offer(db, offer, cancel);
        if (listing.Closed || listing.Expires <= now) return [];
        var prefix = CommentPrefix + offer.ToString("N") + ":";
        var json = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(prefix)).Select(r => r.Data).ToListAsync(cancel);
        return json.Select(s => JsonSerializer.Deserialize<TradeComment>(s)!).OrderByDescending(c => c.Time).ThenBy(c => c.Id).Take(50).Reverse().ToArray();
    }

    public static async Task AddComment(ServerDbContext db, Guid cycle, Guid offer, Guid user, string author, string text, long now, CancellationToken cancel = default)
    {
        author = author.Trim(); text = text.Trim();
        if (user == Guid.Empty || author.Length is < 1 or > 200 || text.Length is < 1 or > 500 || text.Any(c => char.IsControl(c) && c != '\n'))
            throw new InvalidOperationException("invalid_comment");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await RequireTradeCycle(db, cycle, cancel);
        var (_, listing) = await Offer(db, offer, cancel);
        if (listing.Closed || listing.Expires <= now) throw new InvalidOperationException("offer_missing");
        // The global transaction lock also serializes the durable per-user rate limit.
        var rateId = "economy:comment-rate:" + user.ToString("N");
        var rate = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == rateId, cancel);
        if (rate != null && long.Parse(rate.Data, System.Globalization.CultureInfo.InvariantCulture) > now - 10000) throw new InvalidOperationException("comment_rate");
        if (rate == null) { rate = new() { Id = rateId, UserId = user }; db.LagoonEconomyRecords.Add(rate); }
        rate.Data = now.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = CommentPrefix + offer.ToString("N") + ":";
        var rows = await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(prefix)).ToListAsync(cancel);
        if (rows.Count >= 200) db.LagoonEconomyRecords.RemoveRange(rows.OrderBy(r => JsonSerializer.Deserialize<TradeComment>(r.Data)!.Time).Take(rows.Count - 199));
        var comment = new TradeComment(Guid.NewGuid(), user, author, text, now);
        db.LagoonEconomyRecords.Add(new() { Id = prefix + comment.Id.ToString("N"), UserId = user, Data = JsonSerializer.Serialize(comment) });
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
    }
}
