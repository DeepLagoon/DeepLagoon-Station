using System.Threading;
using System.Threading.Tasks;
namespace Content.Server.Database;
public partial interface IServerDbManager
{
    Task<ShipRepairPurchase> PurchaseShipRepairAsync(ShipRepairPurchase request, CancellationToken cancel = default);
    Task<ShipRepairPurchase> FinishShipRepairAsync(Guid id, Guid user, bool refund, CancellationToken cancel = default);
    Task<ShipRepairPurchase[]> ExpiredShipRepairsAsync(long now, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<ShipRepairPurchase> PurchaseShipRepairAsync(ShipRepairPurchase request, CancellationToken cancel = default) => RunDbCommand(() => _db.PurchaseShipRepairAsync(request, cancel));
    public Task<ShipRepairPurchase> FinishShipRepairAsync(Guid id, Guid user, bool refund, CancellationToken cancel = default) => RunDbCommand(() => _db.FinishShipRepairAsync(id, user, refund, cancel));
    public Task<ShipRepairPurchase[]> ExpiredShipRepairsAsync(long now, CancellationToken cancel = default) => RunDbCommand(() => _db.ExpiredShipRepairsAsync(now, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<ShipRepairPurchase> PurchaseShipRepairAsync(ShipRepairPurchase request, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.PurchaseShipRepair(db.DbContext, request, cancel); }
    public async Task<ShipRepairPurchase> FinishShipRepairAsync(Guid id, Guid user, bool refund, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.FinishShipRepair(db.DbContext, id, user, refund, cancel); }
    public async Task<ShipRepairPurchase[]> ExpiredShipRepairsAsync(long now, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ExpiredShipRepairs(db.DbContext, now, cancel); }
}
