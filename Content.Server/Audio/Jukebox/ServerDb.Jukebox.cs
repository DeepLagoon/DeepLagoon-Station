using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<string> GetJukeboxLibraryAsync(NetUserId user);
    Task<bool> SaveJukeboxLibraryAsync(NetUserId user, string json);
}
public sealed partial class ServerDbManager
{
    public Task<string> GetJukeboxLibraryAsync(NetUserId user) => RunDbCommand(() => _db.GetJukeboxLibraryAsync(user));
    public Task<bool> SaveJukeboxLibraryAsync(NetUserId user, string json) => RunDbCommand(() => _db.SaveJukeboxLibraryAsync(user, json));
}
public abstract partial class ServerDbBase
{
    public async Task<string> GetJukeboxLibraryAsync(NetUserId user)
    {
        await using var db = await GetDb();
        return await db.DbContext.Preference.Where(p => p.UserId == user.UserId)
            .Select(p => p.JukeboxLibraryJson).SingleOrDefaultAsync() ?? "{}";
    }
    public async Task<bool> SaveJukeboxLibraryAsync(NetUserId user, string json)
    {
        await using var db = await GetDb();
        return await db.DbContext.Preference.Where(p => p.UserId == user.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.JukeboxLibraryJson, json)) == 1;
    }
}
