using System.Threading;
using System.Threading.Tasks;
namespace Content.Server.Database;
public partial interface IServerDbManager
{
    Task<VoyageJob> ClaimVoyageRefundAsync(Guid id, Guid lease, CancellationToken cancel = default);
    Task<VoyageJob> IssueVoyageCargoAsync(Guid user, int profile, Guid id, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default);
    Task<VoyageCrewRecord> HireVoyageMarketCrewAsync(Guid user, int profile, int slot, string offer, Guid operation, CancellationToken cancel = default);
    Task CloseVoyageWorldAsync(Guid cycle, CancellationToken cancel = default);
    Task<VoyageWorld> ReadVoyageWorldAsync(bool allowNew, string[] routes, long now, CancellationToken cancel = default);
    Task<VoyageAccountState> ReadVoyagesAsync(Guid user, int profile, CancellationToken cancel = default);
    Task<VoyageJob[]> ActiveVoyagesAsync(CancellationToken cancel = default);
    Task HireVoyageCrewAsync(VoyageCrewRecord crew, long cost, CancellationToken cancel = default);
    Task CommitVoyageAsync(VoyageJob job, RecoveryRemoval? removal, CancellationToken cancel = default);
    Task<VoyageJob> AdvanceVoyageAsync(Guid id, long now, CancellationToken cancel = default);
}
public sealed partial class ServerDbManager
{
    public Task<VoyageJob> ClaimVoyageRefundAsync(Guid id, Guid lease, CancellationToken cancel = default) => RunDbCommand(() => _db.ClaimVoyageRefundAsync(id, lease, cancel));
    public Task<VoyageJob> IssueVoyageCargoAsync(Guid user, int profile, Guid id, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default) => RunDbCommand(() => _db.IssueVoyageCargoAsync(user, profile, id, lease, recovery, cancel));
    public Task<VoyageCrewRecord> HireVoyageMarketCrewAsync(Guid user, int profile, int slot, string offer, Guid operation, CancellationToken cancel = default) => RunDbCommand(() => _db.HireVoyageMarketCrewAsync(user, profile, slot, offer, operation, cancel));
    public Task CloseVoyageWorldAsync(Guid cycle, CancellationToken cancel = default) => RunDbCommand(() => _db.CloseVoyageWorldAsync(cycle, cancel));
    public Task<VoyageWorld> ReadVoyageWorldAsync(bool allowNew, string[] routes, long now, CancellationToken cancel = default) => RunDbCommand(() => _db.ReadVoyageWorldAsync(allowNew, routes, now, cancel));
    public Task<VoyageAccountState> ReadVoyagesAsync(Guid user, int profile, CancellationToken cancel = default) => RunDbCommand(() => _db.ReadVoyagesAsync(user, profile, cancel));
    public Task<VoyageJob[]> ActiveVoyagesAsync(CancellationToken cancel = default) => RunDbCommand(() => _db.ActiveVoyagesAsync(cancel));
    public Task HireVoyageCrewAsync(VoyageCrewRecord crew, long cost, CancellationToken cancel = default) => RunDbCommand(() => _db.HireVoyageCrewAsync(crew, cost, cancel));
    public Task CommitVoyageAsync(VoyageJob job, RecoveryRemoval? removal, CancellationToken cancel = default) => RunDbCommand(() => _db.CommitVoyageAsync(job, removal, cancel));
    public Task<VoyageJob> AdvanceVoyageAsync(Guid id, long now, CancellationToken cancel = default) => RunDbCommand(() => _db.AdvanceVoyageAsync(id, now, cancel));
}
public abstract partial class ServerDbBase
{
    public async Task<VoyageJob> ClaimVoyageRefundAsync(Guid id, Guid lease, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ClaimVoyageRefund(db.DbContext, id, lease, cancel); }
    public async Task<VoyageJob> IssueVoyageCargoAsync(Guid user, int profile, Guid id, Guid lease, RecoveryExternal? recovery, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.IssueVoyageCargo(db.DbContext, user, profile, id, lease, recovery, cancel); }
    public async Task<VoyageCrewRecord> HireVoyageMarketCrewAsync(Guid user, int profile, int slot, string offer, Guid operation, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.HireVoyageMarketCrew(db.DbContext, user, profile, slot, offer, operation, cancel); }
    public async Task CloseVoyageWorldAsync(Guid cycle, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.CloseVoyageWorld(db.DbContext, cycle, cancel); }
    public async Task<VoyageWorld> ReadVoyageWorldAsync(bool allowNew, string[] routes, long now, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ReadVoyageWorld(db.DbContext, allowNew, routes, now, cancel); }
    public async Task<VoyageAccountState> ReadVoyagesAsync(Guid user, int profile, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ReadVoyages(db.DbContext, user, profile, cancel); }
    public async Task<VoyageJob[]> ActiveVoyagesAsync(CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.ActiveVoyages(db.DbContext, cancel); }
    public async Task HireVoyageCrewAsync(VoyageCrewRecord crew, long cost, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.HireVoyageCrew(db.DbContext, crew, cost, cancel); }
    public async Task CommitVoyageAsync(VoyageJob job, RecoveryRemoval? removal, CancellationToken cancel = default) { await using var db = await GetDb(cancel); await EconomyOperations.CommitVoyage(db.DbContext, job, removal, cancel); }
    public async Task<VoyageJob> AdvanceVoyageAsync(Guid id, long now, CancellationToken cancel = default) { await using var db = await GetDb(cancel); return await EconomyOperations.AdvanceVoyage(db.DbContext, id, now, cancel); }
}
