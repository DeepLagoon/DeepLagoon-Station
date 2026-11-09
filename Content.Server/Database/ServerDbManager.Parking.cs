using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<int> PayParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default);
    Task<ParkingCredit> CreditParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<ParkingCredit> CreditParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreditParkingAsync(user, slot, amount, operation, cancel));
    }

    public Task<int> PayParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PayParkingAsync(user, slot, amount, operation, cancel));
    }
}
public abstract partial class ServerDbBase
{
    public async Task<ParkingCredit> CreditParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ParkingBankOperations.Credit(db.DbContext, user.UserId, slot, amount, operation, cancel);
    }

    public async Task<int> PayParkingAsync(NetUserId user, int slot, int amount, Guid operation, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ParkingBankOperations.Debit(db.DbContext, user.UserId, slot, amount, operation, cancel);
    }
}
