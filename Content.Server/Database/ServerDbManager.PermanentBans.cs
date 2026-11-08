using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<Guid[]> GetActivePermanentBanUsersAsync();
}

public sealed partial class ServerDbManager
{
    public Task<Guid[]> GetActivePermanentBanUsersAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(_db.GetActivePermanentBanUsersAsync);
    }
}

public abstract partial class ServerDbBase
{
    public async Task<Guid[]> GetActivePermanentBanUsersAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.Ban
            .Where(ban => ban.PlayerUserId != null && ban.ExpirationTime == null && ban.Unban == null)
            .Select(ban => ban.PlayerUserId!.Value)
            .Distinct()
            .ToArrayAsync();
    }
}
