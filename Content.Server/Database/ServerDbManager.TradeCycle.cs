using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<TradeCycle> BeginTradeCycleAsync(bool allowNew, CancellationToken cancel = default);
    Task<TradeCycle> CloseTradeCycleAsync(Guid expected, CancellationToken cancel = default);
}

public sealed partial class ServerDbManager
{
    public Task<TradeCycle> BeginTradeCycleAsync(bool allowNew, CancellationToken cancel = default)
        => RunDbCommand(() => _db.BeginTradeCycleAsync(allowNew, cancel));
    public Task<TradeCycle> CloseTradeCycleAsync(Guid expected, CancellationToken cancel = default)
        => RunDbCommand(() => _db.CloseTradeCycleAsync(expected, cancel));
}

public abstract partial class ServerDbBase
{
    public async Task<TradeCycle> BeginTradeCycleAsync(bool allowNew, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.BeginTradeCycle(db.DbContext, allowNew, cancel);
    }
    public async Task<TradeCycle> CloseTradeCycleAsync(Guid expected, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.CloseTradeCycle(db.DbContext, expected, cancel);
    }
}
