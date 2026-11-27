using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<TradeComment[]> ReadTradeCommentsAsync(Guid offer, long now, CancellationToken cancel = default);
    Task AddTradeCommentAsync(Guid cycle, Guid offer, Guid user, string author, string text, long now, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<TradeComment[]> ReadTradeCommentsAsync(Guid offer, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReadTradeCommentsAsync(offer, now, cancel));
    public Task AddTradeCommentAsync(Guid cycle, Guid offer, Guid user, string author, string text, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.AddTradeCommentAsync(cycle, offer, user, author, text, now, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<TradeComment[]> ReadTradeCommentsAsync(Guid offer, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Comments(db.DbContext, offer, now, cancel);
    }
    public async Task AddTradeCommentAsync(Guid cycle, Guid offer, Guid user, string author, string text, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await EconomyOperations.AddComment(db.DbContext, cycle, offer, user, author, text, now, cancel);
    }
}
