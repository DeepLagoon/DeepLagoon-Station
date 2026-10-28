using Content.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace DeepLagoon.AccountService;

public sealed partial class AccountOperations
{
    private static async Task RequireHost(ServerDbContext db, Guid actor, CancellationToken cancel)
    {
        var admin = await db.Admin.AsNoTracking().Include(a => a.Flags).Include(a => a.AdminRank!)
            .ThenInclude(r => r.Flags).SingleOrDefaultAsync(a => a.UserId == actor, cancel);
        if (!DiscordApiAuthority.Has(admin, "HOST")) throw new AccountError("forbidden", 403);
    }

    private void ReserveBudget(string id, long amount, bool staff = false, string? category = null)
    {
        if (amount <= 0 || amount > (staff ? 1 : settings.MaxGrant)) throw new AccountError("grant_limit", 429);
        using var db = OpenReceipts();
        using var tx = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT amount FROM api_safety_budget WHERE bucket=$bucket AND request_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$bucket", category ?? (staff ? "staff" : "lc"));
        if (command.ExecuteScalar() is long prior)
        {
            if (prior != amount) throw new AccountError("operation_conflict");
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        command.CommandText = "SELECT coalesce(sum(amount),0) FROM api_safety_budget WHERE bucket=$bucket AND created_at>$since";
        command.Parameters.AddWithValue("$since", now - 3600);
        if (Convert.ToInt64(command.ExecuteScalar()) + amount > (staff ? 30 : settings.HourlyGrantLimit))
            throw new AccountError("grant_limit", 429);
        command.Parameters["$since"].Value = now - 86400;
        if (Convert.ToInt64(command.ExecuteScalar()) + amount > (staff ? 100 : settings.DailyGrantLimit))
            throw new AccountError("grant_limit", 429);
        command.CommandText = "INSERT INTO api_safety_budget VALUES($bucket,$id,$amount,$now)";
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        tx.Commit();
    }
}
