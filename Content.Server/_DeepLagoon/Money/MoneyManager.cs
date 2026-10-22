using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Money;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Money;

/// <summary>Ordinary account savings. The database is authoritative; LC is a separate wallet.</summary>
public sealed partial class MoneyManager
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private readonly Dictionary<NetUserId, long> _balances = new();
    // Serialize publication as well as writes: an older read cannot overwrite a committed balance.
    private readonly SemaphoreSlim _operations = new(1, 1);

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgMoney>();
        _net.RegisterNetMessage<MsgMoneyRequest>(msg => SendBalance(msg.MsgChannel));
        _net.Connected += (_, args) => SendBalance(args.Channel);
        _net.Disconnect += (_, args) => _balances.Remove(args.Channel.UserId);
    }

    private async void SendBalance(INetChannel channel)
    {
        try { await GetMoneyBalanceAsync(channel.UserId); }
        catch (Exception e) { Logger.GetSawmill("money").Error( $"Could not fetch Money for {channel.UserId}: {e}"); }
    }

    private void Publish(NetUserId user, long balance)
    {
        _balances[user] = balance;
        if (_players.TryGetSessionById(user, out var session) && session.Channel.IsConnected)
            _net.ServerSendMessage(new MsgMoney { Coins = balance }, session.Channel);
    }

    public long? GetMoneyBalance(NetUserId user) => _balances.TryGetValue(user, out var balance) ? balance : null;

    public long? GetConfirmedMoneyBalance(NetUserId user)
    {
        try
        {
            var task = GetMoneyBalanceAsync(user);
            _tasks.BlockWaitOnTask(task);
            return task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Logger.GetSawmill("money").Error( $"Could not confirm Money for {user}: {e}");
            return null;
        }
    }

    public async Task<long> GetMoneyBalanceAsync(NetUserId user)
    {
        await _operations.WaitAsync();
        try
        {
            var balance = await _db.GetMoneyAsync(user);
            Publish(user, balance);
            return balance;
        }
        finally { _operations.Release(); }
    }

    public async Task SetMoneyBalanceAsync(NetUserId user, long balance)
    {
        await _operations.WaitAsync();
        try { Publish(user, await _db.SetMoneyAsync(user, balance)); }
        finally { _operations.Release(); }
    }

    public async Task<long> AddMoneyAsync(NetUserId user, long amount)
    {
        await _operations.WaitAsync();
        try
        {
            var balance = await _db.AddMoneyAsync(user, amount);
            Publish(user, balance);
            return balance;
        }
        finally { _operations.Release(); }
    }

    public async Task<MoneyTransfer> TransferMoneyAsync(NetUserId sender, NetUserId recipient, long amount)
    {
        await _operations.WaitAsync();
        try
        {
            var result = await _db.TransferMoneyAsync(sender, recipient, amount);
            Publish(sender, result.SenderBalance);
            Publish(recipient, result.RecipientBalance);
            return result;
        }
        finally { _operations.Release(); }
    }

    private async Task<MoneyPayment> PayWithBankAsync(NetUserId user, int bankBalance, int amount, int slot, bool useSavings)
    {
        await _operations.WaitAsync();
        try
        {
            var result = await _db.PayMoneyWithBankAsync(user, bankBalance, amount, profileSlot: slot, useSavings: useSavings);
            Publish(user, result.MoneyBalance);
            return result;
        }
        finally { _operations.Release(); }
    }

    /// <summary>The spawning API is synchronous; confirm payment before equipping purchased gear.</summary>
    public bool TryPayWithBank(NetUserId user, int bankBalance, int amount, int slot, bool useSavings, out int bankCost)
    {
        bankCost = 0;
        try
        {
            var task = PayWithBankAsync(user, bankBalance, amount, slot, useSavings);
            _tasks.BlockWaitOnTask(task);
            bankCost = task.GetAwaiter().GetResult().BankCost;
            return true;
        }
        catch (Exception e)
        {
            Logger.GetSawmill("money").Warning( $"Money payment rejected for {user}: {e.Message}");
            return false;
        }
    }

    public bool TryAddMoney(NetUserId user, long amount)
    {
        try
        {
            var task = AddMoneyAsync(user, amount);
            _tasks.BlockWaitOnTask(task);
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            Logger.GetSawmill("money").Error( $"Money deposit rejected for {user}: {e}");
            return false;
        }
    }

    private async Task DepositWithBankAsync(NetUserId user, int slot, int balance, int bankAmount, long moneyAmount)
    {
        await _operations.WaitAsync();
        try { Publish(user, await _db.DepositMoneyWithBankAsync(user, slot, balance, bankAmount, moneyAmount)); }
        finally { _operations.Release(); }
    }

    public bool TryDepositWithBank(NetUserId user, int slot, int balance, int bankAmount, long moneyAmount)
    {
        try
        {
            var task = DepositWithBankAsync(user, slot, balance, bankAmount, moneyAmount);
            _tasks.BlockWaitOnTask(task);
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            Logger.GetSawmill("money").Error( $"Bank/Money deposit rejected for {user}: {e}");
            return false;
        }
    }
}
