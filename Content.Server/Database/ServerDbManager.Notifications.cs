using System;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<CharacterNotification[]> ReadCharacterNotificationsAsync(Guid user, int profile, bool markRead, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<CharacterNotification[]> ReadCharacterNotificationsAsync(Guid user, int profile, bool markRead, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReadCharacterNotificationsAsync(user, profile, markRead, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<CharacterNotification[]> ReadCharacterNotificationsAsync(Guid user, int profile, bool markRead, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.ReadCharacterNotifications(db.DbContext, user, profile, markRead, cancel);
    }
}
