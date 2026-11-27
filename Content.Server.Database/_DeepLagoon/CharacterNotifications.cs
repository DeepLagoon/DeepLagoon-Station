using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record CharacterNotification(string Id, long Created, string Title, string Text, long Amount = 0, bool Read = false);

public static partial class EconomyOperations
{
    private static string NotificationPrefix(Guid user, int profile) => $"pda:notification:{user:N}:{profile}:";

    // Called inside the owning financial transaction, so a crash cannot lose a paid sale's notice.
    private static async Task NotifyCharacter(ServerDbContext db, Guid user, int profile, Guid operation,
        string stage, string title, string text, long amount, CancellationToken cancel)
    {
        var prefix = NotificationPrefix(user, profile);
        var key = prefix + operation.ToString("N") + ":" + stage;
        if (db.LagoonEconomyRecords.Local.Any(r => r.Id == key) || await db.LagoonEconomyRecords.AnyAsync(r => r.Id == key, cancel)) return;
        var notice = new CharacterNotification(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), title, text, amount);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(notice) });
        var rows = await db.LagoonEconomyRecords.Where(r => r.UserId == user && r.Id.StartsWith(prefix)).ToListAsync(cancel);
        foreach (var expired in rows.OrderByDescending(r => JsonSerializer.Deserialize<CharacterNotification>(r.Data)!.Created).Skip(199))
            db.LagoonEconomyRecords.Remove(expired);
    }

    public static async Task<CharacterNotification[]> ReadCharacterNotifications(ServerDbContext db, Guid user, int profile,
        bool markRead, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        if (markRead) await LockUsers(db, [user], cancel);
        await Profile(db, user, profile, cancel);
        var prefix = NotificationPrefix(user, profile);
        var rows = await db.LagoonEconomyRecords.Where(r => r.UserId == user && r.Id.StartsWith(prefix)).ToArrayAsync(cancel);
        var notices = rows.Select(r => JsonSerializer.Deserialize<CharacterNotification>(r.Data)!).OrderByDescending(n => n.Created).Take(200).ToArray();
        if (markRead)
        {
            foreach (var row in rows)
            {
                var notice = JsonSerializer.Deserialize<CharacterNotification>(row.Data)!;
                if (!notice.Read) row.Data = JsonSerializer.Serialize(notice with { Read = true });
            }
            await db.SaveChangesAsync(cancel);
        }
        await tx.CommitAsync(cancel);
        return notices;
    }
}
