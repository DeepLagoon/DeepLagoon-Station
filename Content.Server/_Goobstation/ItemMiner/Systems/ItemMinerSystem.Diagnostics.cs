using System.Diagnostics;

namespace Content.Server._Goobstation.ItemMiner;

public sealed partial class ItemMinerSystem
{
    private struct MiningFrame
    {
        public int Miners, Active, Due, Attempts, Spawned, Merged, Deferred;
        public long TotalTicks, CheckTicks, SpawnTicks, MergeTicks, EffectTicks;
        public long PendingPayouts;
        public double MaxLateness;
    }

    private static readonly double MillisecondsPerTimestamp = 1000d / Stopwatch.Frequency;
    private static readonly long SummaryPeriod = 60 * Stopwatch.Frequency;
    private static readonly long SlowPeriod = 15 * Stopwatch.Frequency;
    private static readonly long SlowThreshold = Math.Max(1L, Stopwatch.Frequency / 100); // 10 ms.
    private MiningFrame _frame, _slowest;
    private long _windowStarted, _nextSummary, _nextSlowReport, _slowestAt;
    private uint _slowestTick;
    private long _frames, _attempts, _spawned, _merged, _dueSamples, _deferredFrames;
    private long _totalTicks, _checkTicks, _spawnTicks, _mergeTicks, _effectTicks, _maxTicks;
    private long _timerResets, _invalidIntervals, _missedIntervals;
    private long _maxPendingPayouts;
    private int _maxMiners, _maxActive, _maxPending;
    private double _maxLateness;

    private void InitializeDiagnostics()
    {
        _windowStarted = Stopwatch.GetTimestamp();
        _nextSummary = _windowStarted + SummaryPeriod;
        _nextSlowReport = _windowStarted + SlowPeriod;
    }

    private void RecordDiagnostics(long now)
    {
        if (_frame.Miners > 0)
        {
            _frames++;
            _maxMiners = Math.Max(_maxMiners, _frame.Miners);
            _maxActive = Math.Max(_maxActive, _frame.Active);
            _maxPending = Math.Max(_maxPending, _frame.Deferred);
            _maxPendingPayouts = Math.Max(_maxPendingPayouts, _frame.PendingPayouts);
            _maxLateness = Math.Max(_maxLateness, _frame.MaxLateness);
            _attempts += _frame.Attempts;
            _spawned += _frame.Spawned;
            _merged += _frame.Merged;
            _dueSamples += _frame.Due;
            if (_frame.Deferred > 0) _deferredFrames++;
            _totalTicks += _frame.TotalTicks;
            _checkTicks += _frame.CheckTicks;
            _spawnTicks += _frame.SpawnTicks;
            _mergeTicks += _frame.MergeTicks;
            _effectTicks += _frame.EffectTicks;
            _maxTicks = Math.Max(_maxTicks, _frame.TotalTicks);
            if (_frame.TotalTicks >= SlowThreshold && _frame.TotalTicks > _slowest.TotalTicks)
            {
                _slowest = _frame;
                _slowestAt = now;
                _slowestTick = _timing.CurTick.Value;
            }
        }

        // Only scalar counters and timestamp reads above; messages are formatted at most once per window.
        if (now >= _nextSlowReport)
        {
            if (_slowest.TotalTicks > 0)
            {
                Log.Warning($"Slow item miner tick: tick={_slowestTick} age_ms={(now - _slowestAt) * MillisecondsPerTimestamp:F1} elapsed_ms={_slowest.TotalTicks * MillisecondsPerTimestamp:F2} check_ms={_slowest.CheckTicks * MillisecondsPerTimestamp:F2} spawn_ms={_slowest.SpawnTicks * MillisecondsPerTimestamp:F2} merge_ms={_slowest.MergeTicks * MillisecondsPerTimestamp:F2} effects_ms={_slowest.EffectTicks * MillisecondsPerTimestamp:F2} miners={_slowest.Miners} active={_slowest.Active} due={_slowest.Due} attempts={_slowest.Attempts} spawned={_slowest.Spawned} merged={_slowest.Merged} pending={_slowest.Deferred} pending_payouts={_slowest.PendingPayouts} lateness_s={_slowest.MaxLateness:F2}");
            }
            _slowest = default;
            _nextSlowReport = now + SlowPeriod;
        }

        if (now < _nextSummary) return;
        if (_frames > 0)
        {
            Log.Info($"Item miner summary: window_s={(now - _windowStarted) / (double) Stopwatch.Frequency:F1} frames={_frames} miners_max={_maxMiners} active_max={_maxActive} attempts={_attempts} spawned={_spawned} merged={_merged} empty_attempts={_attempts - _spawned} due_samples={_dueSamples} pending_max={_maxPending} pending_payouts_max={_maxPendingPayouts} deferred_frames={_deferredFrames} lateness_max_s={_maxLateness:F2} timer_resets={_timerResets} invalid_intervals={_invalidIntervals} missed_intervals_discarded={_missedIntervals} avg_ms={_totalTicks * MillisecondsPerTimestamp / _frames:F3} max_ms={_maxTicks * MillisecondsPerTimestamp:F2} check_total_ms={_checkTicks * MillisecondsPerTimestamp:F2} spawn_total_ms={_spawnTicks * MillisecondsPerTimestamp:F2} merge_total_ms={_mergeTicks * MillisecondsPerTimestamp:F2} effects_total_ms={_effectTicks * MillisecondsPerTimestamp:F2} attempt_limit={MaxAttemptsPerTick} spawn_budget_ms={SpawnBudget * MillisecondsPerTimestamp:F2}");
        }
        _frames = _attempts = _spawned = _merged = _dueSamples = _deferredFrames = 0;
        _totalTicks = _checkTicks = _spawnTicks = _mergeTicks = _effectTicks = _maxTicks = 0;
        _timerResets = _invalidIntervals = _missedIntervals = 0;
        _maxPendingPayouts = 0;
        _maxMiners = _maxActive = _maxPending = 0;
        _maxLateness = 0;
        _windowStarted = now;
        _nextSummary = now + SummaryPeriod;
    }
}
