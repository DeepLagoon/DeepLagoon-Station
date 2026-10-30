using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;
namespace Content.Server.Database;
public partial interface IServerDbManager
{
    Task<LagoonEconomySnapshot> GetEconomyAsync(NetUserId user, int externalTier, CancellationToken cancel = default);
    Task<LagoonEconomySnapshot> PurchaseEconomyAsync(NetUserId user, LagoonEconomyPurchase purchase, CancellationToken cancel = default);
    Task<LagoonCoinResult> RecordActivityAsync(NetUserId user, LagoonActivity activity, CancellationToken cancel = default);
    Task<List<LagoonPublicSupporter>> GetPaidSupportersAsync(CancellationToken cancel = default);
    Task<List<LagoonReferral>> GetReferralsAsync(NetUserId user, CancellationToken cancel = default);
    Task BindReferralAsync(NetUserId user, string code, CancellationToken cancel = default);
    Task<List<(Guid User, LagoonCoinResult Result)>> ReferralRewardsAsync(NetUserId user, CancellationToken cancel = default);
    Task SetEconomyColorAsync(NetUserId user, string color, int externalTier, string operationKey, CancellationToken cancel = default);
}
public abstract partial class ServerDbBase
{
    public async Task<List<LagoonReferral>> GetReferralsAsync(NetUserId user, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.Referrals(db.DbContext, user.UserId, cancel);
    }
    public async Task BindReferralAsync(NetUserId user, string code, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await AccountEconomyOperations.BindReferral(db.DbContext, user.UserId, code, DateTime.UtcNow, cancel);
    }
    public async Task<List<(Guid User, LagoonCoinResult Result)>> ReferralRewardsAsync(NetUserId user, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.ReferralRewards(db.DbContext, user.UserId, DateTime.UtcNow, cancel);
    }
    public async Task<LagoonEconomySnapshot> GetEconomyAsync(NetUserId user, int externalTier, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.Get(db.DbContext, user.UserId, externalTier, DateTime.UtcNow, cancel);
    }
    public async Task<LagoonEconomySnapshot> PurchaseEconomyAsync(NetUserId user, LagoonEconomyPurchase purchase, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.Purchase(db.DbContext, user.UserId, purchase, DateTime.UtcNow, cancel);
    }
    public async Task<LagoonCoinResult> RecordActivityAsync(NetUserId user, LagoonActivity activity, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.Activity(db.DbContext, user.UserId, activity, DateTime.UtcNow, cancel);
    }
    public async Task<List<LagoonPublicSupporter>> GetPaidSupportersAsync(CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await AccountEconomyOperations.PublicSupporters(db.DbContext, cancel);
    }
    public async Task SetEconomyColorAsync(NetUserId user, string color, int externalTier, string operationKey, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await AccountEconomyOperations.SetColor(db.DbContext, user.UserId, color, externalTier, DateTime.UtcNow, cancel, operationKey: operationKey);
    }
}
public sealed partial class ServerDbManager
{
    public Task<List<LagoonReferral>> GetReferralsAsync(NetUserId user, CancellationToken cancel = default) => RunDbCommand(() => _db.GetReferralsAsync(user, cancel));
    public Task BindReferralAsync(NetUserId user, string code, CancellationToken cancel = default) => RunDbCommand(() => _db.BindReferralAsync(user, code, cancel));
    public Task<List<(Guid User, LagoonCoinResult Result)>> ReferralRewardsAsync(NetUserId user, CancellationToken cancel = default) => RunDbCommand(() => _db.ReferralRewardsAsync(user, cancel));
    public Task<LagoonEconomySnapshot> GetEconomyAsync(NetUserId user, int externalTier, CancellationToken cancel = default) => RunDbCommand(() => _db.GetEconomyAsync(user, externalTier, cancel));
    public Task<LagoonEconomySnapshot> PurchaseEconomyAsync(NetUserId user, LagoonEconomyPurchase purchase, CancellationToken cancel = default) => RunDbCommand(() => _db.PurchaseEconomyAsync(user, purchase, cancel));
    public Task<LagoonCoinResult> RecordActivityAsync(NetUserId user, LagoonActivity activity, CancellationToken cancel = default) => RunDbCommand(() => _db.RecordActivityAsync(user, activity, cancel));
    public Task<List<LagoonPublicSupporter>> GetPaidSupportersAsync(CancellationToken cancel = default) => RunDbCommand(() => _db.GetPaidSupportersAsync(cancel));
    public Task SetEconomyColorAsync(NetUserId user, string color, int externalTier, string operationKey, CancellationToken cancel = default) => RunDbCommand(() => _db.SetEconomyColorAsync(user, color, externalTier, operationKey, cancel));
}
