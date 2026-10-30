using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class AccountEconomyOperations
{
    public static async Task<List<LagoonReferral>> Referrals(ServerDbContext db, Guid user, CancellationToken cancel = default)
    {
        // Filter in SQL before decoding JSON, then verify the actual edge. Never expose other account state.
        var owner = user.ToString();
        var records = await (from account in db.LagoonEconomyRecords.AsNoTracking()
            where account.Id.StartsWith("account:") && account.Data.Contains(owner)
            join player in db.Player.AsNoTracking() on account.UserId equals player.UserId
            select new { account.Data, player.LastSeenUserName }).ToListAsync(cancel);
        return records.Select(row => (State: System.Text.Json.JsonSerializer.Deserialize<LagoonEconomyState>(row.Data), Name: row.LastSeenUserName))
            .Where(row => row.State?.Referrer == user)
            .Select(row => new LagoonReferral(row.Name, row.State!.ReferralStage,
                row.State.ReferrerReward ? row.State.ReferralStage >= 2 ? 100 : row.State.ReferralStage == 1 ? 25 : 0 : 0))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
