using System.Threading;
using System.Threading.Tasks;
namespace Content.Server.Database;
public partial interface IServerDbManager
{
    Task CommitVoyageBasketAsync(VoyageBasketCommit batch, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task CommitVoyageBasketAsync(VoyageBasketCommit batch, CancellationToken cancel = default)
        => RunDbCommand(() => _db.CommitVoyageBasketAsync(batch, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task CommitVoyageBasketAsync(VoyageBasketCommit batch, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await EconomyOperations.CommitVoyageBasket(db.DbContext, batch, cancel);
    }
}
