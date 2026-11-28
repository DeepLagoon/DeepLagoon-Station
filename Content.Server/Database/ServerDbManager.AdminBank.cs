using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<AdminBankResult> AdjustBankByAdminAsync(NetUserId user, AdminBankAdjustment adjustment, CancellationToken cancel = default);
}

public sealed partial class ServerDbManager
{
    public Task<AdminBankResult> AdjustBankByAdminAsync(NetUserId user, AdminBankAdjustment adjustment, CancellationToken cancel = default)
        => RunDbCommand(() => _db.AdjustBankByAdminAsync(user, adjustment, cancel));
}

public abstract partial class ServerDbBase
{
    public async Task<AdminBankResult> AdjustBankByAdminAsync(NetUserId user, AdminBankAdjustment adjustment, CancellationToken cancel = default)
    {
        await using var handle = await GetDb(cancel);
        return await AdminBankOperations.Adjust(handle.DbContext, user.UserId, adjustment, cancel);
    }
}
