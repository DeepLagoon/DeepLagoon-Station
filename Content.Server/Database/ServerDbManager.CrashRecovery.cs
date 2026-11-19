using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<RecoveryBoot> BootCrashRecovery(string scope, Guid run);
    Task WriteCrashRecovery(string scope, Guid run, RecoveryAsset[] assets, RecoveryPurchase[] purchases);
    Task CloseCrashRecovery(string scope, Guid run);
    Task<int?> GetRecoveryProfile(Guid user, int slot);
    Task<RecoveryIncident> ReadCrashRecovery(string scope, Guid run);
    Task<RecoveryClaim> ClaimCrashRecovery(string scope, Guid incident, Guid destination, Guid user, int profile, Guid[] assets, Guid[] refunds, int round = 0);
}

public sealed partial class ServerDbManager
{
    private Task<T> RunRecoveryCommand<T>(Func<Task<T>> command)
        => _synchronous ? RunDbCommand(command) : Task.Run(() => RunDbCommand(command));
    public Task<RecoveryBoot> BootCrashRecovery(string scope, Guid run) => RunRecoveryCommand(() => _db.BootCrashRecovery(scope, run));
    public Task WriteCrashRecovery(string scope, Guid run, RecoveryAsset[] assets, RecoveryPurchase[] purchases)
        => RunDbCommand(() => _db.WriteCrashRecovery(scope, run, assets, purchases));
    public Task CloseCrashRecovery(string scope, Guid run) => RunDbCommand(() => _db.CloseCrashRecovery(scope, run));
    public Task<int?> GetRecoveryProfile(Guid user, int slot) => RunRecoveryCommand(() => _db.GetRecoveryProfile(user, slot));
    public Task<RecoveryIncident> ReadCrashRecovery(string scope, Guid run) => RunRecoveryCommand(() => _db.ReadCrashRecovery(scope, run));
    public Task<RecoveryClaim> ClaimCrashRecovery(string scope, Guid incident, Guid destination, Guid user, int profile, Guid[] assets, Guid[] refunds, int round = 0)
        => RunRecoveryCommand(() => _db.ClaimCrashRecovery(scope, incident, destination, user, profile, assets, refunds, round));
}

public abstract partial class ServerDbBase
{
    public async Task<RecoveryIncident> ReadCrashRecovery(string scope, Guid run)
    {
        await using var db = await GetDb();
        return await CrashRecoveryOperations.ReadIncident(db.DbContext, scope, run);
    }
    public async Task<RecoveryBoot> BootCrashRecovery(string scope, Guid run)
    {
        await using var db = await GetDb();
        return await CrashRecoveryOperations.Boot(db.DbContext, scope, run, DateTime.UtcNow);
    }
    public async Task WriteCrashRecovery(string scope, Guid run, RecoveryAsset[] assets, RecoveryPurchase[] purchases)
    {
        await using var db = await GetDb();
        await CrashRecoveryOperations.Write(db.DbContext, scope, run, assets, purchases, DateTime.UtcNow);
    }
    public async Task CloseCrashRecovery(string scope, Guid run)
    {
        await using var db = await GetDb();
        await CrashRecoveryOperations.Close(db.DbContext, scope, run, DateTime.UtcNow);
    }
    public async Task<int?> GetRecoveryProfile(Guid user, int slot)
    {
        await using var db = await GetDb();
        return await CrashRecoveryOperations.ProfileId(db.DbContext, user, slot);
    }
    public async Task<RecoveryClaim> ClaimCrashRecovery(string scope, Guid incident, Guid destination, Guid user, int profile, Guid[] assets, Guid[] refunds, int round = 0)
    {
        await using var db = await GetDb();
        return await CrashRecoveryOperations.Claim(db.DbContext, scope, incident, destination, user, profile, assets, refunds, round: round);
    }
}
