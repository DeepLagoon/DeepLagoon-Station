using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<Guid> ChangeExchangePasswordAsync(NetUserId user, int slot, ExchangePasswordChange request, CancellationToken cancel = default);
    Task<ExchangeWalletAccess> LoginExchangeWalletAsync(NetUserId user, int slot, ExchangeWalletLogin request, CancellationToken cancel = default);
    Task<ExchangeRecipient> FindExchangeRecipientAsync(string address, CancellationToken cancel = default);
    Task<ExchangeResult> TransferExchangeAsync(NetUserId user, ExchangeTransferRequest request, long now, CancellationToken cancel = default);
    Task<ExchangeResult> ReadExchangeAsync(NetUserId user, int slot, long now, CancellationToken cancel = default, string walletAddress = "");
    Task<ExchangeResult> TradeExchangeAsync(NetUserId user, ExchangeTradeRequest request, long now, CancellationToken cancel = default);
    Task<ExchangeResult> DepositExchangeAsync(NetUserId user, int slot, string asset, long units, Guid operation,
        RecoveryRemoval? recovery, long now, CancellationToken cancel = default, int expectedProfileId = 0, string walletAddress = "", Guid? walletVersion = null);
    Task<string[]> MintExchangeAsync(int count, long now, CancellationToken cancel = default);
    Task<ExchangeMiningResult[]> MintExchangeToWalletsAsync(ExchangeMiningRequest[] requests, long now, CancellationToken cancel = default);
    Task SampleExchangeAsync(ExchangeMaterialSample[] materials, long now, CancellationToken cancel = default);
}

public sealed partial class ServerDbManager
{
    public Task<Guid> ChangeExchangePasswordAsync(NetUserId user, int slot, ExchangePasswordChange request, CancellationToken cancel = default)
        => RunDbCommand(() => _db.ChangeExchangePasswordAsync(user, slot, request, cancel));
    public Task<ExchangeWalletAccess> LoginExchangeWalletAsync(NetUserId user, int slot, ExchangeWalletLogin request, CancellationToken cancel = default)
        => RunDbCommand(() => _db.LoginExchangeWalletAsync(user, slot, request, cancel));
    public Task<ExchangeRecipient> FindExchangeRecipientAsync(string address, CancellationToken cancel = default)
        => RunDbCommand(() => _db.FindExchangeRecipientAsync(address, cancel));
    public Task<ExchangeResult> TransferExchangeAsync(NetUserId user, ExchangeTransferRequest request, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.TransferExchangeAsync(user, request, now, cancel));
    public Task<ExchangeResult> ReadExchangeAsync(NetUserId user, int slot, long now, CancellationToken cancel = default, string walletAddress = "")
        => RunDbCommand(() => _db.ReadExchangeAsync(user, slot, now, cancel, walletAddress));
    public Task<ExchangeResult> TradeExchangeAsync(NetUserId user, ExchangeTradeRequest request, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.TradeExchangeAsync(user, request, now, cancel));
    public Task<ExchangeResult> DepositExchangeAsync(NetUserId user, int slot, string asset, long units, Guid operation,
        RecoveryRemoval? recovery, long now, CancellationToken cancel = default, int expectedProfileId = 0, string walletAddress = "", Guid? walletVersion = null)
        => RunDbCommand(() => _db.DepositExchangeAsync(user, slot, asset, units, operation, recovery, now, cancel, expectedProfileId, walletAddress, walletVersion));
    public Task<string[]> MintExchangeAsync(int count, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.MintExchangeAsync(count, now, cancel));
    public Task<ExchangeMiningResult[]> MintExchangeToWalletsAsync(ExchangeMiningRequest[] requests, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.MintExchangeToWalletsAsync(requests, now, cancel));
    public Task SampleExchangeAsync(ExchangeMaterialSample[] materials, long now, CancellationToken cancel = default)
        => RunDbCommand(() => _db.SampleExchangeAsync(materials, now, cancel));
}

public abstract partial class ServerDbBase
{
    public async Task<Guid> ChangeExchangePasswordAsync(NetUserId user, int slot, ExchangePasswordChange request, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.ChangePassword(db.DbContext, user.UserId, slot, request, cancel);
    }
    public async Task<ExchangeWalletAccess> LoginExchangeWalletAsync(NetUserId user, int slot, ExchangeWalletLogin request, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.LoginAccess(db.DbContext, user.UserId, slot, request, cancel);
    }
    public async Task<ExchangeRecipient> FindExchangeRecipientAsync(string address, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.FindRecipient(db.DbContext, address, cancel);
    }
    public async Task<ExchangeResult> TransferExchangeAsync(NetUserId user, ExchangeTransferRequest request, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.Transfer(db.DbContext, user.UserId, request, now, cancel);
    }
    public async Task<ExchangeResult> ReadExchangeAsync(NetUserId user, int slot, long now, CancellationToken cancel = default, string walletAddress = "")
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.Read(db.DbContext, user.UserId, slot, now, cancel, walletAddress);
    }
    public async Task<ExchangeResult> TradeExchangeAsync(NetUserId user, ExchangeTradeRequest request, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.Trade(db.DbContext, user.UserId, request, now, cancel);
    }
    public async Task<ExchangeResult> DepositExchangeAsync(NetUserId user, int slot, string asset, long units, Guid operation,
        RecoveryRemoval? recovery, long now, CancellationToken cancel = default, int expectedProfileId = 0, string walletAddress = "", Guid? walletVersion = null)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.Deposit(db.DbContext, user.UserId, slot, asset, units, operation, recovery, now, cancel, expectedProfileId, walletAddress, walletVersion);
    }
    public async Task<string[]> MintExchangeAsync(int count, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.Mint(db.DbContext, count, now, cancel);
    }
    public async Task<ExchangeMiningResult[]> MintExchangeToWalletsAsync(ExchangeMiningRequest[] requests, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        return await ExchangeOperations.MintToWallets(db.DbContext, requests, now, cancel);
    }
    public async Task SampleExchangeAsync(ExchangeMaterialSample[] materials, long now, CancellationToken cancel = default)
    {
        await using var db = await GetDb(cancel);
        await ExchangeOperations.Sample(db.DbContext, materials, now, cancel);
    }
}
