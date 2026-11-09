using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<int?> GetCharacterProfileIdAsync(NetUserId user, int slot);
}

public sealed partial class ServerDbManager
{
    public Task<int?> GetCharacterProfileIdAsync(NetUserId user, int slot)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetCharacterProfileIdAsync(user, slot));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<int?> GetCharacterProfileIdAsync(NetUserId user, int slot)
    {
        await using var db = await GetDb();
        return await db.DbContext.Profile
            .Where(profile => profile.Preference.UserId == user.UserId && profile.Slot == slot)
            .Select(profile => (int?) profile.Id)
            .SingleOrDefaultAsync();
    }
}
