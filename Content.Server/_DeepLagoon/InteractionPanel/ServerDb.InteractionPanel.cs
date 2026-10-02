using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<string> GetInteractionPanelActionsAsync(NetUserId user);
    Task<bool> SaveInteractionPanelActionsAsync(NetUserId user, string json);
}

public sealed partial class ServerDbManager
{
    public Task<string> GetInteractionPanelActionsAsync(NetUserId user) => RunDbCommand(() => _db.GetInteractionPanelActionsAsync(user));
    public Task<bool> SaveInteractionPanelActionsAsync(NetUserId user, string json) => RunDbCommand(() => _db.SaveInteractionPanelActionsAsync(user, json));
}

public abstract partial class ServerDbBase
{
    public async Task<string> GetInteractionPanelActionsAsync(NetUserId user)
    {
        await using var db = await GetDb();
        return await db.DbContext.Preference.Where(p => p.UserId == user.UserId)
            .Select(p => p.InteractionPanelActionsJson).SingleOrDefaultAsync() ?? "[]";
    }

    public async Task<bool> SaveInteractionPanelActionsAsync(NetUserId user, string json)
    {
        await using var db = await GetDb();
        // Обновляем только свою колонку: сохранение персонажа не затирает библиотеку и наоборот.
        return await db.DbContext.Preference.Where(p => p.UserId == user.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.InteractionPanelActionsJson, json)) == 1;
    }
}
