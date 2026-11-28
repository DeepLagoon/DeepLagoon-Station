using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<BasketPlan> QuoteTradeBasketAsync(BasketQuery query, long now, CancellationToken cancel = default);
    Task<BasketResult> PurchaseTradeBasketAsync(BasketPurchase request, long now, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<BasketPlan> QuoteTradeBasketAsync(BasketQuery query, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.QuoteTradeBasketAsync(query, now, cancel));
    public Task<BasketResult> PurchaseTradeBasketAsync(BasketPurchase request, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.PurchaseTradeBasketAsync(request, now, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<BasketPlan> QuoteTradeBasketAsync(BasketQuery query, long now, CancellationToken cancel = default)
    { await using var db = await GetDb(cancel); return await EconomyOperations.QuoteBasket(db.DbContext, query, now, cancel); }
    public async Task<BasketResult> PurchaseTradeBasketAsync(BasketPurchase request, long now, CancellationToken cancel = default)
    { await using var db = await GetDb(cancel); return await EconomyOperations.PurchaseBasket(db.DbContext, request, now, cancel); }
}
