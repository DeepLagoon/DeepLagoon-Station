using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<EconomyFinance> SampleSectorEconomyAsync(Guid epoch, long now, int active, EconomyPolicy policy, EconomyPurchase[] purchases, CancellationToken cancel = default);
    Task<EconomyPublic> ReadSectorEconomyAsync(CancellationToken cancel = default);
    Task<EconomyAccount> ReadEconomyAccountAsync(Guid user, int slot, CancellationToken cancel = default);
    Task<EconomyPayment> PayProcurementAsync(EconomyProcurement request, EconomyPolicy policy, long now, CancellationToken cancel = default);
    Task<TradeBoard> ReadTradeBoardAsync(Guid user, int profile, CancellationToken cancel = default);
    Task<TradeResult> CreateTradeOfferAsync(TradeCreate request, long now, CancellationToken cancel = default);
    Task<TradeResult> ExecuteTradeAsync(TradeDeal request, long now, CancellationToken cancel = default);
    Task<TradeResult> CancelTradeOfferAsync(Guid user, int profile, Guid offer, Guid operation, long now, CancellationToken cancel = default);
    Task<TradeDelivery> ClaimTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, long now, CancellationToken cancel = default);
    Task CompleteTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default);
    Task ExpireTradeOffersAsync(long now, CancellationToken cancel = default);
}

public sealed partial class ServerDbManager
{
    public Task<EconomyFinance> SampleSectorEconomyAsync(Guid epoch, long now, int active, EconomyPolicy policy, EconomyPurchase[] purchases, CancellationToken cancel = default)
        => RunDbCommand(() => _db.SampleSectorEconomyAsync(epoch, now, active, policy, purchases, cancel));
    public Task<EconomyPublic> ReadSectorEconomyAsync(CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReadSectorEconomyAsync(cancel));
    public Task<EconomyAccount> ReadEconomyAccountAsync(Guid user, int slot, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReadEconomyAccountAsync(user, slot, cancel));
    public Task<EconomyPayment> PayProcurementAsync(EconomyProcurement request, EconomyPolicy policy, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.PayProcurementAsync(request, policy, now, cancel));
    public Task<TradeBoard> ReadTradeBoardAsync(Guid user, int profile, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ReadTradeBoardAsync(user, profile, cancel));
    public Task<TradeResult> CreateTradeOfferAsync(TradeCreate request, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.CreateTradeOfferAsync(request, now, cancel));
    public Task<TradeResult> ExecuteTradeAsync(TradeDeal request, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ExecuteTradeAsync(request, now, cancel));
    public Task<TradeResult> CancelTradeOfferAsync(Guid user, int profile, Guid offer, Guid operation, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.CancelTradeOfferAsync(user, profile, offer, operation, now, cancel));
    public Task<TradeDelivery> ClaimTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ClaimTradeDeliveryAsync(user, profile, delivery, lease, now, cancel));
    public Task CompleteTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default)
        => RunDbCommand(() => _db.CompleteTradeDeliveryAsync(user, profile, delivery, lease, recovery, cancel));
    public Task ExpireTradeOffersAsync(long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ExpireTradeOffersAsync(now, cancel));
}

public abstract partial class ServerDbBase
{
    public async Task<EconomyFinance> SampleSectorEconomyAsync(Guid epoch, long now, int active, EconomyPolicy policy, EconomyPurchase[] purchases, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Sample(db.DbContext, epoch, now, active, policy, purchases, cancel);
    }
    public async Task<EconomyPublic> ReadSectorEconomyAsync(CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Public(db.DbContext, cancel);
    }
    public async Task<EconomyAccount> ReadEconomyAccountAsync(Guid user, int slot, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Read(db.DbContext, user, slot, cancel);
    }
    public async Task<EconomyPayment> PayProcurementAsync(EconomyProcurement request, EconomyPolicy policy, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Procure(db.DbContext, request, policy, now, cancel);
    }
    public async Task<TradeBoard> ReadTradeBoardAsync(Guid user, int profile, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Board(db.DbContext, user, profile, cancel);
    }
    public async Task<TradeResult> CreateTradeOfferAsync(TradeCreate request, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.CreateOffer(db.DbContext, request, now, cancel);
    }
    public async Task<TradeResult> ExecuteTradeAsync(TradeDeal request, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.Deal(db.DbContext, request, now, cancel);
    }
    public async Task<TradeResult> CancelTradeOfferAsync(Guid user, int profile, Guid offer, Guid operation, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.CancelOffer(db.DbContext, user, profile, offer, operation, now, cancel);
    }
    public async Task<TradeDelivery> ClaimTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await EconomyOperations.ClaimDelivery(db.DbContext, user, profile, delivery, lease, now, cancel);
    }
    public async Task CompleteTradeDeliveryAsync(Guid user, int profile, Guid delivery, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await EconomyOperations.CompleteDelivery(db.DbContext, user, profile, delivery, lease, recovery, cancel);
    }
    public async Task ExpireTradeOffersAsync(long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await EconomyOperations.ExpireOffers(db.DbContext, now, cancel);
    }
}
