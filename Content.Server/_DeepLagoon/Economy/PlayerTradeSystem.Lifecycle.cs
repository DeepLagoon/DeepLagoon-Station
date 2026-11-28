using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.GameTicking;
using Robust.Shared.Asynchronous;

namespace Content.Server._DeepLagoon.Economy;

public sealed partial class PlayerTradeSystem
{
    [Dependency] private ITaskManager _tradeTasks = default!;
    private Guid _tradeCycle;
    private int _tradeGeneration;
    private bool _tradeReady, _tradeLifecycleBusy, _tradeClosePending, _tradeSettling;
    private bool _allowNewTradeCycle = true;
    private TimeSpan _tradeLifecycleRetry;
    internal Guid TradeCycleIdentity => _tradeCycle;
    internal bool TradeLifecycleReady => _tradeReady;

    private void InitializeTradeLifecycle()
    {
        // Do not use startup/restart cleanup or Shutdown: both also occur after a crash.
        SubscribeLocalEvent<RoundEndedEvent>(_ => EndTradeCycle());
        SubscribeLocalEvent<GameRunLevelChangedEvent>(args =>
        {
            if (args.New == GameRunLevel.PostRound || args.Old == GameRunLevel.InRound && args.New == GameRunLevel.PreRoundLobby)
                EndTradeCycle();
            else if (args.New == GameRunLevel.InRound)
            {
                _allowNewTradeCycle = true;
                _tradeGeneration++;
                _tradeLifecycleRetry = TimeSpan.Zero;
            }
        });
    }

    private void EndTradeCycle()
    {
        if (!_allowNewTradeCycle && !_tradeClosePending) return;
        _allowNewTradeCycle = false;
        _tradeReady = false;
        _tradeGeneration++;
        _access.Clear();
        _basketQuotes.Clear();
        _tradeClosePending = true;
        _tradeSettling = true;
        _tradeLifecycleRetry = TimeSpan.Zero;
        // Persist only the small close marker/listing metadata before the end event returns.
        // Item snapshots and refunds are settled in bounded asynchronous batches.
        try
        {
            var task = _db.CloseTradeCycleAsync(_tradeCycle);
            _tradeTasks.BlockWaitOnTask(task);
            _tradeCycle = task.GetAwaiter().GetResult().Id;
            _tradeClosePending = false;
        }
        catch (Exception e) { Log.Error($"Market round closure awaits retry ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)})."); }
    }

    private void UpdateTradeLifecycle()
    {
        if (!_tradeLifecycleBusy && (!_tradeReady && _allowNewTradeCycle && _economy.Enabled || _tradeClosePending || _tradeSettling)
            && _timing.RealTime >= _tradeLifecycleRetry)
            _ = MaintainTradeCycle();
    }

    private async Task MaintainTradeCycle()
    {
        _tradeLifecycleBusy = true;
        var generation = _tradeGeneration;
        _tradeLifecycleRetry = _timing.RealTime + TimeSpan.FromSeconds(2);
        try
        {
            if (_tradeClosePending)
            {
                var closed = await _db.CloseTradeCycleAsync(_tradeCycle);
                _tradeCycle = closed.Id;
                _tradeClosePending = false;
            }
            var cycle = await _db.BeginTradeCycleAsync(_allowNewTradeCycle);
            if (generation != _tradeGeneration) return;
            _tradeCycle = cycle.Id;
            _tradeReady = !cycle.Closed && _allowNewTradeCycle;
            _tradeSettling = cycle.Closed && cycle.PendingOffers;
            if (_tradeSettling)
            {
                await _db.ExpireTradeOffersAsync(SectorEconomySystem.UtcNow);
                _tradeLifecycleRetry = _timing.RealTime + TimeSpan.FromMilliseconds(100);
            }
        }
        catch (Exception e)
        {
            _tradeReady = false;
            Log.Warning($"Market lifecycle awaits retry ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)}).");
        }
        finally { _tradeLifecycleBusy = false; }
    }
}
